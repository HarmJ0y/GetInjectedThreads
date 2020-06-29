﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Security.Principal;
using System.Text;
using System.Diagnostics.Eventing.Reader;
using GetInjectedThreads.Enums;
using GetInjectedThreads.Structs;


namespace GetInjectedThreads
{
    class Program
    {
        // Required Interop functions
        [DllImport("shell32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool IsUserAnAdmin();

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(ProcessAccessFlags processAccess, bool bInheritHandle, int processId);

        [DllImport("kernel32.dll")]
        public static extern bool ReadProcessMemory(int hProcess, int lpBaseAddress, byte[] lpBuffer, int dwSize, ref int lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern int VirtualQueryEx(IntPtr hProcess, IntPtr lpAddress, out MEMORY_BASIC_INFORMATION64 lpBuffer, uint dwLength);

        [DllImport("Kernel32.dll")]
        static extern IntPtr OpenThread(ThreadAccess dwDesiredAccess, bool bInheritHandle, int dwThreadId);

        [DllImport("Advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern Boolean OpenThreadToken(IntPtr ThreadHandle, TokenAccessFlags DesiredAccess, bool OpenAsSelf, out IntPtr TokenHandle);

        [DllImport("Advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern Boolean OpenProcessToken(IntPtr ProcessHandle, TokenAccessFlags DesiredAccess, out IntPtr TokenHandle);

        [DllImport("Advapi32.dll")]
        static extern bool GetTokenInformation(IntPtr TokenHandle, TOKEN_INFORMATION_CLASS TokenInformationClass, IntPtr TokenInformation, int TokenInformationLength, out int ReturnLength);

        [DllImport("ntdll.dll", SetLastError = true)]
        static extern int NtQueryInformationThread(IntPtr threadHandle, ThreadInfoClass threadInformationClass, IntPtr threadInformation, int threadInformationLength, IntPtr returnLengthPtr);

        [DllImport("Advapi32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern int ConvertSidToStringSid(IntPtr pSID, out IntPtr ptrSid);

        [DllImport("Secur32.dll")]
        static extern uint LsaGetLogonSessionData(IntPtr pLUID, out IntPtr ppLogonSessionData);

        [DllImport("Secur32.dll")]
        private static extern uint LsaFreeReturnBuffer(IntPtr buffer);

        [HandleProcessCorruptedStateExceptions]
        static void Main(string[] args)
        {
            // Check if running as administrator first? Or at least check if SeDebugPrivilege enabled?
            if(IsUserAnAdmin() == false)
            {
                Console.WriteLine("Program is not running as Administrator. Exiting...");
                System.Environment.Exit(1);
            }

            List<InjectedThread> injectedThreads = new List<InjectedThread>();

            // Create array of Process objects for each running process
            Process[] runningProcesses = Process.GetProcesses();

            // Iterate over each process and get all threads by ID
            foreach (Process process in runningProcesses)
            {
                // PID 0 and PID 4 aren't valid targets for injection
                if (process.Id != 0 && process.Id != 4)
                {
                    // Get all threads under running process
                    ProcessThreadCollection threadCollection = process.Threads;
                    IntPtr hProcess;

                    try
                    {
                        // Get handle to the process
                        hProcess = OpenProcess(ProcessAccessFlags.All, false, process.Id);
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        Console.WriteLine($"Couldn't get handle to process: {process.Id} - System.ComponentModel.Win32Exception - Access Is Denied");
                        continue;
                    }
                    catch (System.InvalidOperationException)
                    {
                        Console.WriteLine($"Couldn't get handle to process {process.Id} - System.InvalidOperationException - Process has Exited");
                        continue;
                    }


                    // Iterate over each thread under the process
                    foreach (ProcessThread thread in threadCollection)
                    {
                        // Get handle to the thread
                        IntPtr hThread = OpenThread(ThreadAccess.AllAccess, false, thread.Id);

                        // Create buffer to store pointer to the thread's base address - NTQueryInformationThread writes to this buffer
                        IntPtr buf = Marshal.AllocHGlobal(IntPtr.Size);

                        // Retrieve thread's Win32StartAddress - Different to thread.StartAddress
                        Int32 result = NtQueryInformationThread(hThread, ThreadInfoClass.ThreadQuerySetWin32StartAddress, buf, IntPtr.Size, IntPtr.Zero);

                        if(result == 0)
                        {
                            // Need to Marshal Win32 type pointer from CLR type IntPtr to access the thread's base address via pointer
                            IntPtr threadBaseAddress = Marshal.ReadIntPtr(buf);

                            // Retrieve MEMORY_BASIC_INFORMATION struct for each thread - assumes 64bit processes, otherwise need to use MEMORY_BASIC_INFORMATION32
                            MEMORY_BASIC_INFORMATION64 memBasicInfo = new MEMORY_BASIC_INFORMATION64();
                            VirtualQueryEx(hProcess, threadBaseAddress, out memBasicInfo, (uint)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION64)));

                            // Check the State and Type fields for the thread's MEMORY_BASIC_INFORMATION
                            // Resolve to false suggests code running from this thread does not have a corresponding image file on disk, likely code injection
                            if (memBasicInfo.State == MemoryBasicInformationState.MEM_COMMIT && memBasicInfo.Type != MemoryBasicInformationType.MEM_IMAGE)
                            {
                                // Create new InjectedThread object and set initial variables
                                InjectedThread injectedThread = new InjectedThread()
                                {
                                    ProcessName = process.ProcessName,
                                    ProcessID = process.Id,
                                    ThreadId = thread.Id,
                                    BaseAddress = threadBaseAddress,
                                    Path = process.MainModule.FileName,
                                    CommandLine = GetProcessCommandLine(process),
                                    MemoryState = Enum.GetName(typeof(MemoryBasicInformationState), memBasicInfo.State),
                                    MemoryType = Enum.GetName(typeof(MemoryBasicInformationType), memBasicInfo.Type),
                                    MemoryProtection = Enum.GetName(typeof(MemoryBasicInformationProtection), memBasicInfo.Protect),
                                    AllocatedMemoryProtection = Enum.GetName(typeof(MemoryBasicInformationProtection), memBasicInfo.AllocationProtect),
                                    BasePriority = thread.BasePriority,
                                    ThreadStartTime = thread.StartTime
                                };

                                // Get handle to thread token. If Impersonation is not being used, thread will use Process access token
                                // Try OpenThreadToken() - if it fails, use OpenProcessToken()
                                if (OpenThreadToken(hThread, TokenAccessFlags.TOKEN_QUERY, false, out IntPtr hToken) == false)
                                {
                                    int error = Marshal.GetLastWin32Error();
                                    Console.WriteLine($"OpenThreadToken() Error: {error}\nThread ID {thread.Id}\nOpening Process Token instead...");

                                    // Thread doesn't have a unique token
                                    injectedThread.IsUniqueThreadToken = false;

                                    // Open process token instead
                                    if (OpenProcessToken(hProcess, TokenAccessFlags.TOKEN_QUERY, out hToken) == false)
                                    {
                                        error = Marshal.GetLastWin32Error();
                                        Console.WriteLine($"OpenProcessToken() Error: {error}\nProcess ID {process.Id}");
                                    }
                                }
                                else
                                {
                                    injectedThread.IsUniqueThreadToken = true;
                                }

                                // Query process or thread token information
                                injectedThread.SecurityIdentifier = QueryToken(hToken, TOKEN_INFORMATION_CLASS.TokenUser);
                                injectedThread.Privileges = QueryToken(hToken, TOKEN_INFORMATION_CLASS.TokenPrivileges);
                                injectedThread.Integrity = QueryToken(hToken, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel);
                                injectedThread.LogonId = QueryToken(hToken, TOKEN_INFORMATION_CLASS.TokenOrigin);
    
                                // Get logon session information and add it to the InjectedThread object
                                if(!string.IsNullOrEmpty(injectedThread.LogonId))
                                {
                                    GetLogonSessionData(hToken, injectedThread);
                                }
                            }
                        }
                    }
                }
            }
        }    


        // Get commandline for a process using WMI. Catch exceptions where either "Access Denied" or process has exited
        static string GetProcessCommandLine(Process process)
        {
            string commandLine = null;

            try
            {
                // Requres reference to System.Management.dll assembly for WMI class
                using (var searcher = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {process.Id}"))
                {
                    using (var matchEnum = searcher.Get().GetEnumerator())
                    {
                        if (matchEnum.MoveNext())
                        {
                            commandLine = matchEnum.Current["CommandLine"]?.ToString();
                        }
                    }
                }
            }
            // Catch process exited exception
            catch(InvalidOperationException) 
            {
                Console.WriteLine($"Couldn't get CommandLine for PID {process.Id} - Process has exited");
            }

            return commandLine;
        }


        /// <summary>
        /// Extracts Token information from a thread's memory by wrapping GetTokenInformation(). Returns token information specified by the tokenInformationClass param
        /// </summary>
        /// <param name="hToken"></param>
        /// <param name="tokenInformationClass"></param>
        /// <returns>String containing the requested token information</returns>
        static string QueryToken(IntPtr hToken, TOKEN_INFORMATION_CLASS tokenInformationClass)
        {
            /* GetTokenInformation
            * SID              TokenUser           (1)
            * Privileges       TokenPrivileges     (3)
            * LogonSession     TokenOrigin         (17)
            * Integrity        TokenIntegrityLevel (25)
            */

            int tokenInformationLength = 0;

            // First need to get the length of TokenInformation - won't return true
            bool result = GetTokenInformation(hToken, tokenInformationClass, IntPtr.Zero, tokenInformationLength, out tokenInformationLength);
            // Buffer for the struct
            IntPtr tokenInformation = Marshal.AllocHGlobal(tokenInformationLength);

            // Make call to GetTokenInformation() and get particular Struct 
            switch (tokenInformationClass)
            {
                case TOKEN_INFORMATION_CLASS.TokenUser:

                    // Store the requested token information in the buffer
                    result = GetTokenInformation(hToken, TOKEN_INFORMATION_CLASS.TokenUser, tokenInformation, tokenInformationLength, out tokenInformationLength);

                    if (result)
                    {
                        // Marshal the buffer to TOKEN_USER Struct
                        TOKEN_USER tokenUser = (TOKEN_USER)Marshal.PtrToStructure(tokenInformation, typeof(TOKEN_USER));

                        // Extract SID from the TOKEN_USER struct
                        IntPtr pSID = IntPtr.Zero;
                        ConvertSidToStringSid(tokenUser.User.Sid, out pSID);
                        string SID = Marshal.PtrToStringAuto(pSID);

                        return SID;
                    }
                    else { return null; }
                        
                case TOKEN_INFORMATION_CLASS.TokenPrivileges:

                    result = GetTokenInformation(hToken, TOKEN_INFORMATION_CLASS.TokenPrivileges, tokenInformation, tokenInformationLength, out tokenInformationLength);

                    if (result)
                    {
                        TOKEN_PRIVILEGES tokenPrivileges = (TOKEN_PRIVILEGES)Marshal.PtrToStructure(tokenInformation, typeof(TOKEN_PRIVILEGES));

                        StringBuilder stringBuilder = new StringBuilder();

                        for (int i = 0; i < tokenPrivileges.PrivilegeCount; i++)
                        {
                            // Bitwise AND comparison to check that each token privilege attribute for SE_PRIVILEGE_ENABLED
                            if (((LUID_ATTRIBUTES)tokenPrivileges.Privileges[i].Attributes & LUID_ATTRIBUTES.SE_PRIVILEGE_ENABLED) == LUID_ATTRIBUTES.SE_PRIVILEGE_ENABLED)
                            {
                                // Append the privilege to the stringBuilder
                                stringBuilder.Append($", {tokenPrivileges.Privileges[i].Luid.LowPart.ToString()}");
                            }
                        }

                        return stringBuilder.ToString().Remove(0, 2);
                    }
                    else { return null; }
                
                case TOKEN_INFORMATION_CLASS.TokenIntegrityLevel:

                    // Mandatory Level SIDs for QueryToken()
                    // https://support.microsoft.com/en-au/help/243330/well-known-security-identifiers-in-windows-operating-systems#allsids
                    Dictionary<string, string> tokenIntegritySIDs = new Dictionary<string, string>
                    {
                        {"S-1-16-0", "Untrusted Mandatory Level"},
                        {"S-1-16-4096", "Low Mandatory Level"},
                        {"S-1-16-8192", "Medium Mandatory Level"},
                        {"S-1-16-8448", "Medium Plus Mandatory Level"},
                        {"S-1-16-12288", "High Mandatory Level"},
                        {"S-1-16-16384", "System Mandatory Level"},
                        {"S-1-16-20480", "Protected Process Mandatory Level"},
                        {"S-1-16-28672", "Secure Process Mandatory Level"}
                    };

                    result = GetTokenInformation(hToken, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, tokenInformation, tokenInformationLength, out tokenInformationLength);

                    if(result)
                    {
                        TOKEN_MANDATORY_LABEL tokenMandatoryLabel = (TOKEN_MANDATORY_LABEL)Marshal.PtrToStructure(tokenInformation, typeof(TOKEN_MANDATORY_LABEL));

                        // Extract SID string from TOKEN_MANDATORY_LABEL
                        IntPtr pSID = IntPtr.Zero;
                        ConvertSidToStringSid(tokenMandatoryLabel.label.Sid, out pSID);
                        string SID = Marshal.PtrToStringAuto(pSID);

                        if (tokenIntegritySIDs.ContainsKey(SID))
                        {
                            return tokenIntegritySIDs[SID];
                        }
                        else { return null; }
                    }
                    else
                    {
                        return null;
                    }

                case TOKEN_INFORMATION_CLASS.TokenOrigin:

                    result = GetTokenInformation(hToken, TOKEN_INFORMATION_CLASS.TokenOrigin, tokenInformation, tokenInformationLength, out tokenInformationLength);

                    if(result)
                    {
                        TOKEN_ORIGIN tokenOrigin = (TOKEN_ORIGIN)Marshal.PtrToStructure(tokenInformation, typeof(TOKEN_ORIGIN));
                        string logonId = tokenOrigin.OriginatingLogonSession.LowPart.ToString();
                        return logonId;
                    }
                    else { return null; }
            }
            return null;
        }

        /// <summary>
        /// Get SECURITY_LOGON_SESSION_DATA for a process or thread via a handle to its token and populate an InjectedThread object's Logon Session values
        /// </summary>
        /// <param name="hToken"></param>
        /// <param name="injectedThread"></param>
        static void GetLogonSessionData(IntPtr hToken, InjectedThread injectedThread)
        {
            int tokenInformationLength = 0;
            bool result = GetTokenInformation(hToken, TOKEN_INFORMATION_CLASS.TokenOrigin, IntPtr.Zero, tokenInformationLength, out tokenInformationLength);
            IntPtr tokenInformation = Marshal.AllocHGlobal(tokenInformationLength);

            result = GetTokenInformation(hToken, TOKEN_INFORMATION_CLASS.TokenOrigin, tokenInformation, tokenInformationLength, out tokenInformationLength);

            if(result)
            {
                // GetTokenInformation to retreive LUID struct
                TOKEN_ORIGIN tokenOrigin = (TOKEN_ORIGIN)Marshal.PtrToStructure(tokenInformation, typeof(TOKEN_ORIGIN));
                IntPtr pLUID = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(LUID)));  
                
                // Get pointer to LUID struct for LsaGetLogonSessionData
                Marshal.StructureToPtr(tokenOrigin.OriginatingLogonSession, pLUID, false);

                IntPtr pLogonSessionData = IntPtr.Zero;
                LsaGetLogonSessionData(pLUID, out pLogonSessionData);

                SECURITY_LOGON_SESSION_DATA logonSessionData = (SECURITY_LOGON_SESSION_DATA)Marshal.PtrToStructure(pLogonSessionData, typeof(SECURITY_LOGON_SESSION_DATA));

                // Check for a valid logon 
                if(logonSessionData.PSiD != IntPtr.Zero)
                {
                    // Win32 systemdate
                    DateTime systime = new DateTime(1601, 1, 1, 0, 0, 0, 0); 

                    // Add logon session information to InjectedThread object
                    string domain = Marshal.PtrToStringUni(logonSessionData.LoginDomain.buffer).Trim();
                    string username = Marshal.PtrToStringUni(logonSessionData.Username.buffer).Trim();
                    injectedThread.Username = $"{domain}\\{username}";
                    injectedThread.LogonSessionStartTime = systime.AddTicks((long)logonSessionData.LoginTime);
                    injectedThread.LogonType = Enum.GetName(typeof(SECURITY_LOGON_TYPES), logonSessionData.LogonType);  
                    injectedThread.AuthenticationPackage = Marshal.PtrToStringAuto(logonSessionData.AuthenticationPackage.buffer);
                }

            }
        }
    }
}
