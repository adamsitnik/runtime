// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

/*++

Module Name:

    pal.h

Abstract:

    CoreCLR Platform Adaptation Layer (PAL) header file.  This file
    defines all types and API calls required by the CoreCLR when
    compiled for Unix-like systems.

    Defines which control the behavior of this include file:
      UNICODE - define it to set the Ansi/Unicode neutral names to
                be the ...W names.  Otherwise the neutral names default
                to be the ...A names.
      PAL_IMPLEMENTATION - define it when implementing the PAL.  Otherwise
                leave it undefined when consuming the PAL.

    Note:  some fields in structs have been renamed from the original
    SDK documentation names, with _PAL_Undefined appended.  This leaves
    the structure layout identical to its Win32 version, but prevents
    PAL consumers from inadvertently referencing undefined fields.

    If you want to add a PAL_ wrapper function to a native function in
    here, you also need to edit palinternal.h and win32pal.h.

--*/

#ifndef __PAL_H__
#define __PAL_H__

#include <float.h>
#include <limits.h>
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <stdarg.h>
#include <stdint.h>
#include <string.h>
#include <math.h>
#include <strings.h>
#include <errno.h>
#include <ctype.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <unistd.h>
#include <wctype.h>
#if defined(__has_include)

#if __has_include(<alloca.h>)
#include <alloca.h>
#endif // __has_include(alloca.h)
#endif // defined(__has_include)

#ifdef __cplusplus
extern "C++"
{

#include <new>

}
#endif

#ifdef  __cplusplus
extern "C" {
#endif

// This macro is used to standardize the wide character string literals between UNIX and Windows.
// Unix L"" is UTF32, and on windows it's UTF16.  Because of built-in assumptions on the size
// of string literals, it's important to match behaviour between Unix and Windows.  Unix will be defined
// as u"" (char16_t)
#define W(str)  u##str

// Undefine the QUOTE_MACRO_L helper and redefine it in terms of u.
// The reason that we do this is that quote macro is defined in ndp\common\inc,
// not inside of coreclr sources.

#define QUOTE_MACRO_L(x) QUOTE_MACRO_u(x)
#define QUOTE_MACRO_u_HELPER(x)     u###x
#define QUOTE_MACRO_u(x)            QUOTE_MACRO_u_HELPER(x)

#include <pal_error.h>
#include <pal_mstypes.h>
#include <minipal/utils.h>

// Native system libray handle.
// On Unix systems, NATIVE_LIBRARY_HANDLE type represents a library handle not registered with the PAL.
typedef PVOID NATIVE_LIBRARY_HANDLE;

#if defined(HOST_ARM64)
// Flag to check if atomics feature is available on
// the machine
extern bool g_arm64_atomics_present;
#endif

/******************* ABI-specific glue *******************************/

#define MAX_PATH 260
#define _MAX_DRIVE  3   /* max. length of drive component */
#define _MAX_DIR    256 /* max. length of path component */
#define _MAX_FNAME  256 /* max. length of file name component */
#define _MAX_EXT    256 /* max. length of extension component */

// In some Win32 APIs MAX_PATH is used for file names (even though 256 is the normal file system limit)
// use _MAX_PATH_FNAME to indicate these cases
#define MAX_PATH_FNAME MAX_PATH
#define MAX_LONGPATH   1024  /* max. length of full pathname */

#define MAXLONG       0x7fffffff
#define MAXDWORD      0xffffffff

//  Sorting IDs.
//
//  Note that the named locale APIs (eg CompareStringExEx) are recommended.
//

#define LANG_ENGLISH                     0x09

/******************* Compiler-specific glue *******************************/
#if defined(_MSC_VER)
#define DECLSPEC_ALIGN(x)   __declspec(align(x))
#else
#define DECLSPEC_ALIGN(x)   __attribute__ ((aligned(x)))
#endif

#define DECLSPEC_NORETURN   PAL_NORETURN

#define EMPTY_BASES_DECL

#if !defined(_MSC_VER) || defined(SOURCE_FORMATTING)
#if __has_builtin(__builtin_assume)
#define __assume(condition) do { bool assume_cond = (condition); __builtin_assume(assume_cond); } while (0)
#else
#define __assume(condition) do { if (!(condition)) __builtin_unreachable(); } while (0)
#endif // __has_builtin(__builtin_assume)

#define __annotation(x)
#endif //!MSC_VER

#define UNALIGNED

#ifndef FORCEINLINE
#if _MSC_VER < 1200
#define FORCEINLINE inline
#else
#define FORCEINLINE __forceinline
#endif
#endif

#ifndef NOOPT_ATTRIBUTE
#if defined(__llvm__)
#define NOOPT_ATTRIBUTE optnone
#else
#define NOOPT_ATTRIBUTE optimize("O0")
#endif
#endif

#ifndef __has_cpp_attribute
#define __has_cpp_attribute(x) (0)
#endif

/******************* PAL-Specific Entrypoints *****************************/

#define DLL_PROCESS_ATTACH 1
#define DLL_THREAD_ATTACH  2
#define DLL_THREAD_DETACH  3
#define DLL_PROCESS_DETACH 0

#define PAL_INITIALIZE_NONE                         0x00
#define PAL_INITIALIZE_SYNC_THREAD                  0x01
#define PAL_INITIALIZE_EXEC_ALLOCATOR               0x02
#define PAL_INITIALIZE_STD_HANDLES                  0x04
#define PAL_INITIALIZE_REGISTER_SIGTERM_HANDLER     0x08
#define PAL_INITIALIZE_DEBUGGER_EXCEPTIONS          0x10
#define PAL_INITIALIZE_ENSURE_STACK_SIZE            0x20
#define PAL_INITIALIZE_REGISTER_SIGNALS             0x40
#define PAL_INITIALIZE_REGISTER_ACTIVATION_SIGNAL   0x80
#define PAL_INITIALIZE_FLUSH_PROCESS_WRITE_BUFFERS  0x100

// PAL_Initialize() flags
#define PAL_INITIALIZE                 (PAL_INITIALIZE_SYNC_THREAD | \
                                        PAL_INITIALIZE_STD_HANDLES)

// PAL_InitializeDLL() flags - don't start any of the helper threads or register any exceptions
#define PAL_INITIALIZE_DLL              PAL_INITIALIZE_NONE

// PAL_InitializeCoreCLR() flags
#define PAL_INITIALIZE_CORECLR         (PAL_INITIALIZE | \
                                        PAL_INITIALIZE_EXEC_ALLOCATOR | \
                                        PAL_INITIALIZE_REGISTER_SIGTERM_HANDLER | \
                                        PAL_INITIALIZE_DEBUGGER_EXCEPTIONS | \
                                        PAL_INITIALIZE_ENSURE_STACK_SIZE | \
                                        PAL_INITIALIZE_REGISTER_SIGNALS | \
                                        PAL_INITIALIZE_REGISTER_ACTIVATION_SIGNAL  | \
                                        PAL_INITIALIZE_FLUSH_PROCESS_WRITE_BUFFERS)

typedef DWORD (PALAPI_NOEXPORT *PTHREAD_START_ROUTINE)(LPVOID lpThreadParameter);
typedef PTHREAD_START_ROUTINE LPTHREAD_START_ROUTINE;

/******************* PAL-Specific Entrypoints *****************************/

PALIMPORT
int
PALAPI
PAL_Initialize(
    int argc,
    char * const argv[]);

PALIMPORT
int
PALAPI
PAL_InitializeDLL();

PALIMPORT
void
PALAPI
PAL_SetInitializeDLLFlags(
    DWORD flags);

PALIMPORT
DWORD
PALAPI
PAL_InitializeCoreCLR(
    const char *szExePath, BOOL runningInExe);

/// <summary>
/// This function shuts down PAL WITHOUT exiting the current process.
/// </summary>
PALIMPORT
void
PALAPI
PAL_Shutdown(
    void);

/// <summary>
/// This function shuts down PAL and exits the current process.
/// </summary>
PALIMPORT
void
PALAPI
PAL_Terminate(
    void);

/// <summary>
/// This function shuts down PAL and exits the current process with
/// the specified exit code.
/// </summary>
PALIMPORT
void
PALAPI
PAL_TerminateEx(
    int exitCode);

typedef VOID (*PSHUTDOWN_CALLBACK)(bool isExecutingOnAltStack);

PALIMPORT
VOID
PALAPI
PAL_SetShutdownCallback(
    IN PSHUTDOWN_CALLBACK callback);

/// <summary>
/// Used by the single-file and native AOT hosts to connect the linked in version of createdump
/// </summary>
typedef int (*PCREATEDUMP_CALLBACK)(const int argc, const char* argv[]);

PALIMPORT
VOID
PALAPI
PAL_SetCreateDumpCallback(
    IN PCREATEDUMP_CALLBACK callback);

PALIMPORT
BOOL
PALAPI
PAL_GenerateCoreDump(
    IN LPCSTR dumpName,
    IN INT dumpType,
    IN ULONG32 flags,
    LPSTR errorMessageBuffer,
    INT cbErrorMessageBuffer);

typedef VOID (*PPAL_STARTUP_CALLBACK)(
    char *modulePath,
    HMODULE hModule,
    PVOID parameter);

PALIMPORT
DWORD
PALAPI
PAL_RegisterForRuntimeStartup(
    IN DWORD dwProcessId,
    IN LPCWSTR lpApplicationGroupId,
    IN PPAL_STARTUP_CALLBACK pfnCallback,
    IN PVOID parameter,
    OUT PVOID *ppUnregisterToken);

PALIMPORT
DWORD
PALAPI
PAL_UnregisterForRuntimeStartup(
    IN PVOID pUnregisterToken);

PALIMPORT
BOOL
PALAPI
PAL_NotifyRuntimeStarted();

PALIMPORT
LPCSTR
PALAPI
PAL_GetApplicationGroupId();

static const unsigned int MAX_DEBUGGER_TRANSPORT_PIPE_NAME_LENGTH = MAX_PATH;

PALIMPORT
VOID
PALAPI
PAL_GetTransportName(
    const unsigned int MAX_TRANSPORT_NAME_LENGTH,
    OUT char *name,
    IN const char *prefix,
    IN DWORD id,
    IN const char *applicationGroupId,
    IN const char *suffix);

PALIMPORT
VOID
PALAPI
PAL_GetTransportPipeName(
    OUT char *name,
    IN DWORD id,
    IN const char *applicationGroupId,
    IN const char *suffix);

PALIMPORT
void
PALAPI
PAL_IgnoreProfileSignal(int signalNum);

PALIMPORT
HINSTANCE
PALAPI
PAL_RegisterModule(
    IN LPCSTR lpLibFileName);

PALIMPORT
VOID
PALAPI
PAL_UnregisterModule(
    IN HINSTANCE hInstance);

PALIMPORT
BOOL
PALAPI
PAL_OpenProcessMemory(
    IN DWORD processId,
    OUT DWORD* pHandle
);

PALIMPORT
VOID
PALAPI
PAL_CloseProcessMemory(
    IN DWORD handle
);

PALIMPORT
BOOL
PALAPI
PAL_ReadProcessMemory(
    IN DWORD handle,
    IN ULONG64 address,
    IN LPVOID buffer,
    IN SIZE_T size,
    OUT SIZE_T* numberOfBytesRead
);

PALIMPORT
BOOL
PALAPI
PAL_ProbeMemory(
    PVOID pBuffer,
    DWORD cbBuffer,
    BOOL fWriteAccess);

PALIMPORT
int
PALAPI
// Start the jitdump file
PAL_PerfJitDump_Start(const char* path);

PALIMPORT
bool
PALAPI
PAL_PerfJitDump_IsStarted();

PALIMPORT
int
PALAPI
// Log a method to the jitdump file.
PAL_PerfJitDump_LogMethod(void* pCode, size_t codeSize, const char* symbol, void* debugInfo, void* unwindInfo);

PALIMPORT
int
PALAPI
// Finish the jitdump file
PAL_PerfJitDump_Finish();

/******************* winuser.h Entrypoints *******************************/

#define MB_OKCANCEL             0x00000001L
#define MB_ABORTRETRYIGNORE     0x00000002L

#define MB_ICONEXCLAMATION      0x00000030L

#define MB_TASKMODAL            0x00002000L

#define MB_DEFAULT_DESKTOP_ONLY     0x00020000L

#define IDOK                    1
#define IDCANCEL                2
#define IDABORT                 3
#define IDRETRY                 4

// From win32.h
#ifndef _CRTIMP
#ifdef __GNUC__
#define _CRTIMP
#else // __GNUC__
#define _CRTIMP __declspec(dllimport)
#endif // __GNUC__
#endif // _CRTIMP

/******************* winbase.h Entrypoints and defines ************************/
typedef struct _SECURITY_ATTRIBUTES {
            DWORD nLength;
            LPVOID lpSecurityDescriptor;
            BOOL bInheritHandle;
} SECURITY_ATTRIBUTES, *PSECURITY_ATTRIBUTES, *LPSECURITY_ATTRIBUTES;

#define _SH_DENYWR      0x20    /* deny write mode */

#define FILE_READ_DATA            ( 0x0001 )    // file & pipe
#define FILE_APPEND_DATA          ( 0x0004 )    // file

#define GENERIC_READ               (0x80000000L)
#define GENERIC_WRITE              (0x40000000L)

#define FILE_SHARE_READ            0x00000001
#define FILE_SHARE_WRITE           0x00000002
#define FILE_SHARE_DELETE          0x00000004

#define CREATE_NEW                 1
#define CREATE_ALWAYS              2
#define OPEN_EXISTING              3
#define OPEN_ALWAYS                4
#define TRUNCATE_EXISTING          5

#define FILE_ATTRIBUTE_READONLY                 0x00000001
#define FILE_ATTRIBUTE_HIDDEN                   0x00000002
#define FILE_ATTRIBUTE_SYSTEM                   0x00000004
#define FILE_ATTRIBUTE_DIRECTORY                0x00000010
#define FILE_ATTRIBUTE_ARCHIVE                  0x00000020
#define FILE_ATTRIBUTE_DEVICE                   0x00000040
#define FILE_ATTRIBUTE_NORMAL                   0x00000080

#define FILE_FLAG_WRITE_THROUGH    0x80000000
#define FILE_FLAG_NO_BUFFERING     0x20000000
#define FILE_FLAG_RANDOM_ACCESS    0x10000000
#define FILE_FLAG_SEQUENTIAL_SCAN  0x08000000
#define FILE_FLAG_BACKUP_SEMANTICS 0x02000000

#define FILE_BEGIN                 0
#define FILE_CURRENT               1
#define FILE_END                   2

#define STILL_ACTIVE (0x00000103L)

#define INVALID_SET_FILE_POINTER   ((DWORD)-1)


PALIMPORT
HANDLE
PALAPI
CreateFileW(
        IN LPCWSTR lpFileName,
        IN DWORD dwDesiredAccess,
        IN DWORD dwShareMode,
        IN LPSECURITY_ATTRIBUTES lpSecurityAttributes,
        IN DWORD dwCreationDisposition,
        IN DWORD dwFlagsAndAttributes,
        IN HANDLE hTemplateFile);

#ifdef UNICODE
#define CreateFile CreateFileW
#else
#define CreateFile CreateFileA
#endif


PALIMPORT
DWORD
PALAPI
SearchPathW(
    IN LPCWSTR lpPath,
    IN LPCWSTR lpFileName,
    IN LPCWSTR lpExtension,
    IN DWORD nBufferLength,
    OUT LPWSTR lpBuffer,
    OUT LPWSTR *lpFilePart
    );

#define SearchPath  SearchPathW

typedef struct _OVERLAPPED {
    ULONG_PTR Internal;
    ULONG_PTR InternalHigh;
    DWORD Offset;
    DWORD OffsetHigh;
    HANDLE  hEvent;
} OVERLAPPED, *LPOVERLAPPED;

PALIMPORT
BOOL
PALAPI
WriteFile(
      IN HANDLE hFile,
      IN LPCVOID lpBuffer,
      IN DWORD nNumberOfBytesToWrite,
      OUT LPDWORD lpNumberOfBytesWritten,
      IN LPOVERLAPPED lpOverlapped);

PALIMPORT
BOOL
PALAPI
ReadFile(
     IN HANDLE hFile,
     OUT LPVOID lpBuffer,
     IN DWORD nNumberOfBytesToRead,
     OUT LPDWORD lpNumberOfBytesRead,
     IN LPOVERLAPPED lpOverlapped);

#define STD_INPUT_HANDLE         ((DWORD)-10)
#define STD_OUTPUT_HANDLE        ((DWORD)-11)
#define STD_ERROR_HANDLE         ((DWORD)-12)

PALIMPORT
HANDLE
PALAPI
GetStdHandle(
         IN DWORD nStdHandle);

PALIMPORT
DWORD
PALAPI
SetFilePointer(
           IN HANDLE hFile,
           IN LONG lDistanceToMove,
           IN PLONG lpDistanceToMoveHigh,
           IN DWORD dwMoveMethod);

PALIMPORT
BOOL
PALAPI
SetFilePointerEx(
           IN HANDLE hFile,
           IN LARGE_INTEGER liDistanceToMove,
           OUT PLARGE_INTEGER lpNewFilePointer,
           IN DWORD dwMoveMethod);

PALIMPORT
DWORD
PALAPI
GetFileSize(
        IN HANDLE hFile,
        OUT LPDWORD lpFileSizeHigh);

PALIMPORT
BOOL
PALAPI GetFileSizeEx(
        IN   HANDLE hFile,
        OUT  PLARGE_INTEGER lpFileSize);

PALIMPORT
VOID
PALAPI
GetSystemTimeAsFileTime(
            OUT LPFILETIME lpSystemTimeAsFileTime);

typedef struct _SYSTEMTIME {
    WORD wYear;
    WORD wMonth;
    WORD wDayOfWeek;
    WORD wDay;
    WORD wHour;
    WORD wMinute;
    WORD wSecond;
    WORD wMilliseconds;
} SYSTEMTIME, *PSYSTEMTIME, *LPSYSTEMTIME;

PALIMPORT
VOID
PALAPI
GetSystemTime(
          OUT LPSYSTEMTIME lpSystemTime);

PALIMPORT
BOOL
PALAPI
FileTimeToSystemTime(
            IN CONST FILETIME *lpFileTime,
            OUT LPSYSTEMTIME lpSystemTime);



PALIMPORT
BOOL
PALAPI
FlushFileBuffers(
         IN HANDLE hFile);

PALIMPORT
UINT
PALAPI
GetConsoleOutputCP();

PALIMPORT
DWORD
PALAPI
GetFullPathNameW(
         IN LPCWSTR lpFileName,
         IN DWORD nBufferLength,
         OUT LPWSTR lpBuffer,
         OUT LPWSTR *lpFilePart);

#ifdef UNICODE
#define GetFullPathName GetFullPathNameW
#else
#define GetFullPathName GetFullPathNameA
#endif

PALIMPORT
DWORD
PALAPI
GetTempPathW(
         IN DWORD nBufferLength,
         OUT LPWSTR lpBuffer);

PALIMPORT
DWORD
PALAPI
GetTempPathA(
         IN DWORD nBufferLength,
         OUT LPSTR lpBuffer);


#ifdef UNICODE
#define GetTempPath GetTempPathW
#else
#define GetTempPath GetTempPathA
#endif

PALIMPORT
HANDLE
PALAPI
CreateSemaphoreExW(
        IN LPSECURITY_ATTRIBUTES lpSemaphoreAttributes,
        IN LONG lInitialCount,
        IN LONG lMaximumCount,
        IN LPCWSTR lpName,
        IN /*_Reserved_*/  DWORD dwFlags,
        IN DWORD dwDesiredAccess);

PALIMPORT
HANDLE
PALAPI
OpenSemaphoreW(
    IN DWORD dwDesiredAccess,
    IN BOOL bInheritHandle,
    IN LPCWSTR lpName);

#define CreateSemaphoreEx CreateSemaphoreExW

PALIMPORT
BOOL
PALAPI
ReleaseSemaphore(
         IN HANDLE hSemaphore,
         IN LONG lReleaseCount,
         OUT LPLONG lpPreviousCount);

PALIMPORT
HANDLE
PALAPI
CreateEventW(
         IN LPSECURITY_ATTRIBUTES lpEventAttributes,
         IN BOOL bManualReset,
         IN BOOL bInitialState,
         IN LPCWSTR lpName);

PALIMPORT
HANDLE
PALAPI
CreateEventExW(
         IN LPSECURITY_ATTRIBUTES lpEventAttributes,
         IN LPCWSTR lpName,
         IN DWORD dwFlags,
         IN DWORD dwDesiredAccess);

// CreateEventExW: dwFlags
#define CREATE_EVENT_MANUAL_RESET ((DWORD)0x1)
#define CREATE_EVENT_INITIAL_SET ((DWORD)0x2)

#define CreateEvent CreateEventW

PALIMPORT
BOOL
PALAPI
SetEvent(
     IN HANDLE hEvent);

PALIMPORT
BOOL
PALAPI
ResetEvent(
       IN HANDLE hEvent);

PALIMPORT
HANDLE
PALAPI
OpenEventW(
       IN DWORD dwDesiredAccess,
       IN BOOL bInheritHandle,
       IN LPCWSTR lpName);

#ifdef UNICODE
#define OpenEvent OpenEventW
#endif

PALIMPORT
HANDLE
PALAPI
CreateMutexW(
    IN LPSECURITY_ATTRIBUTES lpMutexAttributes,
    IN BOOL bInitialOwner,
    IN LPCWSTR lpName);

PALIMPORT
HANDLE
PALAPI
CreateMutexExW(
    IN LPSECURITY_ATTRIBUTES lpMutexAttributes,
    IN LPCWSTR lpName,
    IN DWORD dwFlags,
    IN DWORD dwDesiredAccess);

PALIMPORT
HANDLE
PALAPI
PAL_CreateMutexW(
    IN BOOL bInitialOwner,
    IN LPCWSTR lpName,
    IN BOOL bCurrentUserOnly,
    IN LPSTR lpSystemCallErrors,
    IN DWORD dwSystemCallErrorsBufferSize);

// CreateMutexExW: dwFlags
#define CREATE_MUTEX_INITIAL_OWNER ((DWORD)0x1)

#define CreateMutex CreateMutexW

PALIMPORT
HANDLE
PALAPI
OpenMutexW(
       IN DWORD dwDesiredAccess,
       IN BOOL bInheritHandle,
       IN LPCWSTR lpName);

PALIMPORT
HANDLE
PALAPI
PAL_OpenMutexW(
       IN LPCWSTR lpName,
       IN BOOL bCurrentUserOnly,
       IN LPSTR lpSystemCallErrors,
       IN DWORD dwSystemCallErrorsBufferSize);

#ifdef UNICODE
#define OpenMutex  OpenMutexW
#endif

PALIMPORT
BOOL
PALAPI
ReleaseMutex(
    IN HANDLE hMutex);

PALIMPORT
DWORD
PALAPI
GetCurrentProcessId();

PALIMPORT
HANDLE
PALAPI
GetCurrentProcess();

PALIMPORT
DWORD
PALAPI
GetCurrentThreadId();

PALIMPORT
size_t
PALAPI
PAL_GetCurrentOSThreadId();

// To work around multiply-defined symbols in the Carbon framework.
#define GetCurrentThread PAL_GetCurrentThread
PALIMPORT
HANDLE
PALAPI
GetCurrentThread();


#define STARTF_USESTDHANDLES       0x00000100

typedef struct _STARTUPINFOW {
    DWORD cb;
    LPWSTR lpReserved_PAL_Undefined;
    LPWSTR lpDesktop_PAL_Undefined;
    LPWSTR lpTitle_PAL_Undefined;
    DWORD dwX_PAL_Undefined;
    DWORD dwY_PAL_Undefined;
    DWORD dwXSize_PAL_Undefined;
    DWORD dwYSize_PAL_Undefined;
    DWORD dwXCountChars_PAL_Undefined;
    DWORD dwYCountChars_PAL_Undefined;
    DWORD dwFillAttribute_PAL_Undefined;
    DWORD dwFlags;
    WORD wShowWindow_PAL_Undefined;
    WORD cbReserved2_PAL_Undefined;
    LPBYTE lpReserved2_PAL_Undefined;
    HANDLE hStdInput;
    HANDLE hStdOutput;
    HANDLE hStdError;
} STARTUPINFOW, *LPSTARTUPINFOW;

typedef STARTUPINFOW STARTUPINFO;
typedef LPSTARTUPINFOW LPSTARTUPINFO;

#define CREATE_NEW_CONSOLE          0x00000010

#define NORMAL_PRIORITY_CLASS             0x00000020

typedef struct _PROCESS_INFORMATION {
    HANDLE hProcess;
    HANDLE hThread;
    DWORD dwProcessId;
    DWORD dwThreadId_PAL_Undefined;
} PROCESS_INFORMATION, *PPROCESS_INFORMATION, *LPPROCESS_INFORMATION;

PALIMPORT
BOOL
PALAPI
CreateProcessW(
           IN LPCWSTR lpApplicationName,
           IN LPWSTR lpCommandLine,
           IN LPSECURITY_ATTRIBUTES lpProcessAttributes,
           IN LPSECURITY_ATTRIBUTES lpThreadAttributes,
           IN BOOL bInheritHandles,
           IN DWORD dwCreationFlags,
           IN LPVOID lpEnvironment,
           IN LPCWSTR lpCurrentDirectory,
           IN LPSTARTUPINFOW lpStartupInfo,
           OUT LPPROCESS_INFORMATION lpProcessInformation);

#define CreateProcess CreateProcessW

PALIMPORT
PAL_NORETURN
VOID
PALAPI
ExitProcess(
        IN UINT uExitCode);

PALIMPORT
BOOL
PALAPI
TerminateProcess(
         IN HANDLE hProcess,
         IN UINT uExitCode);

PALIMPORT
BOOL
PALAPI
GetExitCodeProcess(
           IN HANDLE hProcess,
           IN LPDWORD lpExitCode);

#define MAXIMUM_WAIT_OBJECTS  64
#define WAIT_OBJECT_0 0
#define WAIT_ABANDONED   0x00000080
#define WAIT_ABANDONED_0 0x00000080
#define WAIT_TIMEOUT 258
#define WAIT_FAILED ((DWORD)0xFFFFFFFF)

#define INFINITE 0xFFFFFFFF // Infinite timeout

PALIMPORT
DWORD
PALAPI
WaitForSingleObject(
            IN HANDLE hHandle,
            IN DWORD dwMilliseconds);

PALIMPORT
DWORD
PALAPI
PAL_WaitForSingleObjectPrioritized(
            IN HANDLE hHandle,
            IN DWORD dwMilliseconds);

PALIMPORT
DWORD
PALAPI
WaitForSingleObjectEx(
            IN HANDLE hHandle,
            IN DWORD dwMilliseconds,
            IN BOOL bAlertable);

PALIMPORT
DWORD
PALAPI
WaitForMultipleObjects(
               IN DWORD nCount,
               IN CONST HANDLE *lpHandles,
               IN BOOL bWaitAll,
               IN DWORD dwMilliseconds);

PALIMPORT
DWORD
PALAPI
WaitForMultipleObjectsEx(
             IN DWORD nCount,
             IN CONST HANDLE *lpHandles,
             IN BOOL bWaitAll,
             IN DWORD dwMilliseconds,
             IN BOOL bAlertable);

PALIMPORT
DWORD
PALAPI
SignalObjectAndWait(
    IN HANDLE hObjectToSignal,
    IN HANDLE hObjectToWaitOn,
    IN DWORD dwMilliseconds,
    IN BOOL bAlertable);

#define DUPLICATE_CLOSE_SOURCE      0x00000001
#define DUPLICATE_SAME_ACCESS       0x00000002

PALIMPORT
BOOL
PALAPI
DuplicateHandle(
        IN HANDLE hSourceProcessHandle,
        IN HANDLE hSourceHandle,
        IN HANDLE hTargetProcessHandle,
        OUT LPHANDLE lpTargetHandle,
        IN DWORD dwDesiredAccess,
        IN BOOL bInheritHandle,
        IN DWORD dwOptions);

PALIMPORT
VOID
PALAPI
Sleep(
      IN DWORD dwMilliseconds);

PALIMPORT
DWORD
PALAPI
SleepEx(
    IN DWORD dwMilliseconds,
    IN BOOL bAlertable);

PALIMPORT
BOOL
PALAPI
SwitchToThread();

#define DEBUG_PROCESS                     0x00000001
#define DEBUG_ONLY_THIS_PROCESS           0x00000002
#define CREATE_SUSPENDED                  0x00000004
#define STACK_SIZE_PARAM_IS_A_RESERVATION 0x00010000

PALIMPORT
HANDLE
PALAPI
CreateThread(
         IN LPSECURITY_ATTRIBUTES lpThreadAttributes,
         IN DWORD dwStackSize,
         IN LPTHREAD_START_ROUTINE lpStartAddress,
         IN LPVOID lpParameter,
         IN DWORD dwCreationFlags,
         OUT LPDWORD lpThreadId);

PALIMPORT
HANDLE
PALAPI
PAL_CreateThread64(
    IN LPSECURITY_ATTRIBUTES lpThreadAttributes,
    IN DWORD dwStackSize,
    IN LPTHREAD_START_ROUTINE lpStartAddress,
    IN LPVOID lpParameter,
    IN DWORD dwCreationFlags,
    OUT SIZE_T* pThreadId);

PALIMPORT
PAL_NORETURN
VOID
PALAPI
ExitThread(
       IN DWORD dwExitCode);

PALIMPORT
DWORD
PALAPI
ResumeThread(
         IN HANDLE hThread);

typedef VOID (PALAPI_NOEXPORT *PAPCFUNC)(ULONG_PTR dwParam);

PALIMPORT
DWORD
PALAPI
QueueUserAPC(
         IN PAPCFUNC pfnAPC,
         IN HANDLE hThread,
         IN ULONG_PTR dwData);

#ifdef HOST_X86

//
// ***********************************************************************************
//
// NOTE: These context definitions are replicated in ndp/clr/src/debug/inc/DbgTargetContext.h (for the
// purposes manipulating contexts from different platforms during remote debugging). Be sure to keep those
// definitions in sync if you make any changes here.
//
// ***********************************************************************************
//

#define SIZE_OF_80387_REGISTERS      80

#define CONTEXT_i386            0x00010000
#define CONTEXT_CONTROL         (CONTEXT_i386 | 0x00000001L) // SS:SP, CS:IP, FLAGS, BP
#define CONTEXT_INTEGER         (CONTEXT_i386 | 0x00000002L) // AX, BX, CX, DX, SI, DI
#define CONTEXT_SEGMENTS        (CONTEXT_i386 | 0x00000004L)
#define CONTEXT_FLOATING_POINT  (CONTEXT_i386 | 0x00000008L) // 387 state
#define CONTEXT_DEBUG_REGISTERS (CONTEXT_i386 | 0x00000010L)

#define CONTEXT_FULL (CONTEXT_CONTROL | CONTEXT_INTEGER | CONTEXT_SEGMENTS)
#define CONTEXT_EXTENDED_REGISTERS  (CONTEXT_i386 | 0x00000020L)
#define CONTEXT_ALL (CONTEXT_CONTROL | CONTEXT_INTEGER | CONTEXT_SEGMENTS | CONTEXT_FLOATING_POINT | CONTEXT_DEBUG_REGISTERS | CONTEXT_EXTENDED_REGISTERS)

#define MAXIMUM_SUPPORTED_EXTENSION     512

#define CONTEXT_XSTATE (CONTEXT_i386 | 0x40L)

#define CONTEXT_EXCEPTION_ACTIVE 0x8000000L
#define CONTEXT_SERVICE_ACTIVE 0x10000000L
#define CONTEXT_EXCEPTION_REQUEST 0x40000000L
#define CONTEXT_EXCEPTION_REPORTING 0x80000000L

//
// This flag is set by the unwinder if it has unwound to a call
// site, and cleared whenever it unwinds through a trap frame.
// It is used by language-specific exception handlers to help
// differentiate exception scopes during dispatching.
//

#define CONTEXT_UNWOUND_TO_CALL 0x20000000

typedef struct _FLOATING_SAVE_AREA {
    DWORD   ControlWord;
    DWORD   StatusWord;
    DWORD   TagWord;
    DWORD   ErrorOffset;
    DWORD   ErrorSelector;
    DWORD   DataOffset;
    DWORD   DataSelector;
    BYTE    RegisterArea[SIZE_OF_80387_REGISTERS];
    DWORD   Cr0NpxState;
} FLOATING_SAVE_AREA;

typedef FLOATING_SAVE_AREA *PFLOATING_SAVE_AREA;

typedef struct _CONTEXT {
    ULONG ContextFlags;

    ULONG   Dr0_PAL_Undefined;
    ULONG   Dr1_PAL_Undefined;
    ULONG   Dr2_PAL_Undefined;
    ULONG   Dr3_PAL_Undefined;
    ULONG   Dr6_PAL_Undefined;
    ULONG   Dr7_PAL_Undefined;

    FLOATING_SAVE_AREA FloatSave;

    ULONG   SegGs_PAL_Undefined;
    ULONG   SegFs_PAL_Undefined;
    ULONG   SegEs_PAL_Undefined;
    ULONG   SegDs_PAL_Undefined;

    ULONG   Edi;
    ULONG   Esi;
    ULONG   Ebx;
    ULONG   Edx;
    ULONG   Ecx;
    ULONG   Eax;

    ULONG   Ebp;
    ULONG   Eip;
    ULONG   SegCs;
    ULONG   EFlags;
    ULONG   Esp;
    ULONG   SegSs;

    UCHAR   ExtendedRegisters[MAXIMUM_SUPPORTED_EXTENSION];
} CONTEXT, *PCONTEXT, *LPCONTEXT;

// To support saving and loading xmm register context we need to know the offset in the ExtendedRegisters
// section at which they are stored. This has been determined experimentally since I have found no
// documentation thus far but it corresponds to the offset we'd expect if a fxsave instruction was used to
// store the regular FP state along with the XMM registers at the start of the extended registers section.
// Technically the offset doesn't really matter if no code in the PAL or runtime knows what the offset should
// be either (as long as we're consistent across GetThreadContext() and SetThreadContext() and we don't
// support any other values in the ExtendedRegisters) but we might as well be as accurate as we can.
#define CONTEXT_EXREG_XMM_OFFSET 160

typedef struct _KNONVOLATILE_CONTEXT {

    DWORD Edi;
    DWORD Esi;
    DWORD Ebx;
    DWORD Ebp;

} KNONVOLATILE_CONTEXT, *PKNONVOLATILE_CONTEXT;

typedef struct _KNONVOLATILE_CONTEXT_POINTERS {

    // The ordering of these fields should be aligned with that
    // of corresponding fields in CONTEXT
    //
    // (See FillRegDisplay in inc/regdisp.h for details)
    PDWORD Edi;
    PDWORD Esi;
    PDWORD Ebx;
    PDWORD Edx;
    PDWORD Ecx;
    PDWORD Eax;

    PDWORD Ebp;

} KNONVOLATILE_CONTEXT_POINTERS, *PKNONVOLATILE_CONTEXT_POINTERS;


//
// Context Frame
//
//
// The flags field within this record controls the contents of a CONTEXT
// record.
//
// If the context record is used as an input parameter, then for each
// portion of the context record controlled by a flag whose value is
// set, it is assumed that such portion of the context record contains
// valid context. If the context record is being used to modify a threads
// context, then only that portion of the threads context is modified.
//
// If the context record is used as an output parameter to capture the
// context of a thread, then only those portions of the thread's context
// corresponding to set flags will be returned.
//

#elif defined(HOST_AMD64)

// copied from winnt.h

#define CONTEXT_AMD64   0x100000

#define CONTEXT_CONTROL (CONTEXT_AMD64 | 0x1L)
#define CONTEXT_INTEGER (CONTEXT_AMD64 | 0x2L)
#define CONTEXT_SEGMENTS (CONTEXT_AMD64 | 0x4L)
#define CONTEXT_FLOATING_POINT  (CONTEXT_AMD64 | 0x8L)
#define CONTEXT_DEBUG_REGISTERS (CONTEXT_AMD64 | 0x10L)

#define CONTEXT_FULL (CONTEXT_CONTROL | CONTEXT_INTEGER | CONTEXT_FLOATING_POINT)

#define CONTEXT_ALL (CONTEXT_CONTROL | CONTEXT_INTEGER | CONTEXT_SEGMENTS | CONTEXT_FLOATING_POINT | CONTEXT_DEBUG_REGISTERS)

#define CONTEXT_XSTATE (CONTEXT_AMD64 | 0x40L)

#define CONTEXT_EXCEPTION_ACTIVE 0x8000000
#define CONTEXT_SERVICE_ACTIVE 0x10000000
#define CONTEXT_EXCEPTION_REQUEST 0x40000000
#define CONTEXT_EXCEPTION_REPORTING 0x80000000

#define XSTATE_GSSE (2)
#define XSTATE_AVX (XSTATE_GSSE)
#define XSTATE_AVX512_KMASK (5)
#define XSTATE_AVX512_ZMM_H (6)
#define XSTATE_AVX512_ZMM (7)
#define XSTATE_APX (19)

#define XSTATE_MASK_GSSE (UI64(1) << (XSTATE_GSSE))
#define XSTATE_MASK_AVX (XSTATE_MASK_GSSE)
#define XSTATE_MASK_AVX512 ((UI64(1) << (XSTATE_AVX512_KMASK)) | \
                            (UI64(1) << (XSTATE_AVX512_ZMM_H)) | \
                            (UI64(1) << (XSTATE_AVX512_ZMM)))
#define XSTATE_MASK_APX (UI64(1) << (XSTATE_APX))

typedef struct DECLSPEC_ALIGN(16) _M128A {
    ULONGLONG Low;
    LONGLONG High;
} M128A, *PM128A;

typedef struct DECLSPEC_ALIGN(16) _M256 {
    M128A Low;
    M128A High;
} M256, *PM256;

typedef struct DECLSPEC_ALIGN(16) _M512 {
    M256 Low;
    M256 High;
} M512, *PM512;

typedef struct _XMM_SAVE_AREA32 {
    WORD   ControlWord;
    WORD   StatusWord;
    BYTE  TagWord;
    BYTE  Reserved1;
    WORD   ErrorOpcode;
    DWORD ErrorOffset;
    WORD   ErrorSelector;
    WORD   Reserved2;
    DWORD DataOffset;
    WORD   DataSelector;
    WORD   Reserved3;
    DWORD MxCsr;
    DWORD MxCsr_Mask;
    M128A FloatRegisters[8];
    M128A XmmRegisters[16];
    BYTE  Reserved4[96];
} XMM_SAVE_AREA32, *PXMM_SAVE_AREA32;

typedef struct DECLSPEC_ALIGN(16) _CONTEXT {

    _CONTEXT() = default;
    _CONTEXT(const _CONTEXT& ctx)
    {
        *this = ctx;
    }

    _CONTEXT& operator=(const _CONTEXT& ctx);

    //
    // Register parameter home addresses.
    //
    // N.B. These fields are for convience - they could be used to extend the
    //      context record in the future.
    //

    DWORD64 P1Home;
    DWORD64 P2Home;
    DWORD64 P3Home;
    DWORD64 P4Home;
    DWORD64 P5Home;
    DWORD64 P6Home;

    //
    // Control flags.
    //

    DWORD ContextFlags;
    DWORD MxCsr;

    //
    // Segment Registers and processor flags.
    //

    WORD   SegCs;
    WORD   SegDs;
    WORD   SegEs;
    WORD   SegFs;
    WORD   SegGs;
    WORD   SegSs;
    DWORD EFlags;

    //
    // Debug registers
    //

    DWORD64 Dr0;
    DWORD64 Dr1;
    DWORD64 Dr2;
    DWORD64 Dr3;
    DWORD64 Dr6;
    DWORD64 Dr7;

    //
    // Integer registers.
    //

    DWORD64 Rax;
    DWORD64 Rcx;
    DWORD64 Rdx;
    DWORD64 Rbx;
    DWORD64 Rsp;
    DWORD64 Rbp;
    DWORD64 Rsi;
    DWORD64 Rdi;
    DWORD64 R8;
    DWORD64 R9;
    DWORD64 R10;
    DWORD64 R11;
    DWORD64 R12;
    DWORD64 R13;
    DWORD64 R14;
    DWORD64 R15;

    //
    // Program counter.
    //

    DWORD64 Rip;

    //
    // Floating point state.
    //

    union {
        XMM_SAVE_AREA32 FltSave;
        struct {
            M128A Header[2];
            M128A Legacy[8];
            M128A Xmm0;
            M128A Xmm1;
            M128A Xmm2;
            M128A Xmm3;
            M128A Xmm4;
            M128A Xmm5;
            M128A Xmm6;
            M128A Xmm7;
            M128A Xmm8;
            M128A Xmm9;
            M128A Xmm10;
            M128A Xmm11;
            M128A Xmm12;
            M128A Xmm13;
            M128A Xmm14;
            M128A Xmm15;
        };
    };

    //
    // Vector registers.
    //

    M128A VectorRegister[26];
    DWORD64 VectorControl;

    //
    // Special debug control registers.
    //

    DWORD64 DebugControl;
    DWORD64 LastBranchToRip;
    DWORD64 LastBranchFromRip;
    DWORD64 LastExceptionToRip;
    DWORD64 LastExceptionFromRip;

    // XSTATE
    DWORD64 XStateFeaturesMask;
    DWORD64 XStateReserved0;

    // XSTATE_AVX
    struct {
        M128A Ymm0H;
        M128A Ymm1H;
        M128A Ymm2H;
        M128A Ymm3H;
        M128A Ymm4H;
        M128A Ymm5H;
        M128A Ymm6H;
        M128A Ymm7H;
        M128A Ymm8H;
        M128A Ymm9H;
        M128A Ymm10H;
        M128A Ymm11H;
        M128A Ymm12H;
        M128A Ymm13H;
        M128A Ymm14H;
        M128A Ymm15H;
    };

    // XSTATE_AVX512_KMASK
    struct {
        DWORD64 KMask0;
        DWORD64 KMask1;
        DWORD64 KMask2;
        DWORD64 KMask3;
        DWORD64 KMask4;
        DWORD64 KMask5;
        DWORD64 KMask6;
        DWORD64 KMask7;
    };

    // XSTATE_AVX512_ZMM_H
    struct {
        M256 Zmm0H;
        M256 Zmm1H;
        M256 Zmm2H;
        M256 Zmm3H;
        M256 Zmm4H;
        M256 Zmm5H;
        M256 Zmm6H;
        M256 Zmm7H;
        M256 Zmm8H;
        M256 Zmm9H;
        M256 Zmm10H;
        M256 Zmm11H;
        M256 Zmm12H;
        M256 Zmm13H;
        M256 Zmm14H;
        M256 Zmm15H;
    };

    // XSTATE_AVX512_ZMM
    struct {
        M512 Zmm16;
        M512 Zmm17;
        M512 Zmm18;
        M512 Zmm19;
        M512 Zmm20;
        M512 Zmm21;
        M512 Zmm22;
        M512 Zmm23;
        M512 Zmm24;
        M512 Zmm25;
        M512 Zmm26;
        M512 Zmm27;
        M512 Zmm28;
        M512 Zmm29;
        M512 Zmm30;
        M512 Zmm31;
    };

    struct
    {
        DWORD64 R16;
        DWORD64 R17;
        DWORD64 R18;
        DWORD64 R19;
        DWORD64 R20;
        DWORD64 R21;
        DWORD64 R22;
        DWORD64 R23;
        DWORD64 R24;
        DWORD64 R25;
        DWORD64 R26;
        DWORD64 R27;
        DWORD64 R28;
        DWORD64 R29;
        DWORD64 R30;
        DWORD64 R31;
    };

} CONTEXT, *PCONTEXT, *LPCONTEXT;

//
// Nonvolatile context pointer record.
//

typedef struct _KNONVOLATILE_CONTEXT_POINTERS {
    union {
        PM128A FloatingContext[16];
        struct {
            PM128A Xmm0;
            PM128A Xmm1;
            PM128A Xmm2;
            PM128A Xmm3;
            PM128A Xmm4;
            PM128A Xmm5;
            PM128A Xmm6;
            PM128A Xmm7;
            PM128A Xmm8;
            PM128A Xmm9;
            PM128A Xmm10;
            PM128A Xmm11;
            PM128A Xmm12;
            PM128A Xmm13;
            PM128A Xmm14;
            PM128A Xmm15;
        } ;
    } ;

    union {
        PDWORD64 IntegerContext[16];
        struct {
            PDWORD64 Rax;
            PDWORD64 Rcx;
            PDWORD64 Rdx;
            PDWORD64 Rbx;
            PDWORD64 Rsp;
            PDWORD64 Rbp;
            PDWORD64 Rsi;
            PDWORD64 Rdi;
            PDWORD64 R8;
            PDWORD64 R9;
            PDWORD64 R10;
            PDWORD64 R11;
            PDWORD64 R12;
            PDWORD64 R13;
            PDWORD64 R14;
            PDWORD64 R15;
        } ;
    } ;

} KNONVOLATILE_CONTEXT_POINTERS, *PKNONVOLATILE_CONTEXT_POINTERS;

#elif defined(HOST_ARM)

#define CONTEXT_ARM   0x00200000L

// end_wx86

#define CONTEXT_CONTROL (CONTEXT_ARM | 0x1L)
#define CONTEXT_INTEGER (CONTEXT_ARM | 0x2L)
#define CONTEXT_FLOATING_POINT  (CONTEXT_ARM | 0x4L)
#define CONTEXT_DEBUG_REGISTERS (CONTEXT_ARM | 0x8L)

#define CONTEXT_FULL (CONTEXT_CONTROL | CONTEXT_INTEGER | CONTEXT_FLOATING_POINT)

#define CONTEXT_ALL (CONTEXT_CONTROL | CONTEXT_INTEGER | CONTEXT_FLOATING_POINT | CONTEXT_DEBUG_REGISTERS)

#define CONTEXT_EXCEPTION_ACTIVE 0x8000000L
#define CONTEXT_SERVICE_ACTIVE 0x10000000L
#define CONTEXT_EXCEPTION_REQUEST 0x40000000L
#define CONTEXT_EXCEPTION_REPORTING 0x80000000L

//
// This flag is set by the unwinder if it has unwound to a call
// site, and cleared whenever it unwinds through a trap frame.
// It is used by language-specific exception handlers to help
// differentiate exception scopes during dispatching.
//

#define CONTEXT_UNWOUND_TO_CALL 0x20000000

//
// Specify the number of breakpoints and watchpoints that the OS
// will track. Architecturally, ARM supports up to 16. In practice,
// however, almost no one implements more than 4 of each.
//

#define ARM_MAX_BREAKPOINTS     8
#define ARM_MAX_WATCHPOINTS     1

typedef struct _NEON128 {
    ULONGLONG Low;
    LONGLONG High;
} NEON128, *PNEON128;

typedef struct DECLSPEC_ALIGN(8) _CONTEXT {

    //
    // Control flags.
    //

    DWORD ContextFlags;

    //
    // Integer registers
    //

    DWORD R0;
    DWORD R1;
    DWORD R2;
    DWORD R3;
    DWORD R4;
    DWORD R5;
    DWORD R6;
    DWORD R7;
    DWORD R8;
    DWORD R9;
    DWORD R10;
    DWORD R11;
    DWORD R12;

    //
    // Control Registers
    //

    DWORD Sp;
    DWORD Lr;
    DWORD Pc;
    DWORD Cpsr;

    //
    // Floating Point/NEON Registers
    //

    DWORD Fpscr;
    DWORD Padding;
    union {
        NEON128 Q[16];
        ULONGLONG D[32];
        DWORD S[32];
    };

    //
    // Debug registers
    //

    DWORD Bvr[ARM_MAX_BREAKPOINTS];
    DWORD Bcr[ARM_MAX_BREAKPOINTS];
    DWORD Wvr[ARM_MAX_WATCHPOINTS];
    DWORD Wcr[ARM_MAX_WATCHPOINTS];

    DWORD Padding2[2];

} CONTEXT, *PCONTEXT, *LPCONTEXT;

//
// Nonvolatile context pointer record.
//

typedef struct _KNONVOLATILE_CONTEXT_POINTERS {

    PDWORD R4;
    PDWORD R5;
    PDWORD R6;
    PDWORD R7;
    PDWORD R8;
    PDWORD R9;
    PDWORD R10;
    PDWORD R11;
    PDWORD Lr;

    PULONGLONG D8;
    PULONGLONG D9;
    PULONGLONG D10;
    PULONGLONG D11;
    PULONGLONG D12;
    PULONGLONG D13;
    PULONGLONG D14;
    PULONGLONG D15;

} KNONVOLATILE_CONTEXT_POINTERS, *PKNONVOLATILE_CONTEXT_POINTERS;

typedef struct _IMAGE_ARM_RUNTIME_FUNCTION_ENTRY {
    DWORD BeginAddress;
    DWORD EndAddress;
    union {
        DWORD UnwindData;
        struct {
            DWORD Flag : 2;
            DWORD FunctionLength : 11;
            DWORD Ret : 2;
            DWORD H : 1;
            DWORD Reg : 3;
            DWORD R : 1;
            DWORD L : 1;
            DWORD C : 1;
            DWORD StackAdjust : 10;
        };
    };
} IMAGE_ARM_RUNTIME_FUNCTION_ENTRY, * PIMAGE_ARM_RUNTIME_FUNCTION_ENTRY;

#elif defined(HOST_ARM64)

#define CONTEXT_ARM64   0x00400000L

#define CONTEXT_CONTROL (CONTEXT_ARM64 | 0x1L)
#define CONTEXT_INTEGER (CONTEXT_ARM64 | 0x2L)
#define CONTEXT_FLOATING_POINT  (CONTEXT_ARM64 | 0x4L)
#define CONTEXT_DEBUG_REGISTERS (CONTEXT_ARM64 | 0x8L)

#define CONTEXT_FULL (CONTEXT_CONTROL | CONTEXT_INTEGER | CONTEXT_FLOATING_POINT)

#define CONTEXT_ALL (CONTEXT_CONTROL | CONTEXT_INTEGER | CONTEXT_FLOATING_POINT | CONTEXT_DEBUG_REGISTERS)

#define CONTEXT_EXCEPTION_ACTIVE 0x8000000L
#define CONTEXT_SERVICE_ACTIVE 0x10000000L
#define CONTEXT_EXCEPTION_REQUEST 0x40000000L
#define CONTEXT_EXCEPTION_REPORTING 0x80000000L

#define CONTEXT_ARM64_XSTATE (CONTEXT_ARM64 | 0x20L)
#define CONTEXT_XSTATE CONTEXT_ARM64_XSTATE

#define XSTATE_ARM64_SVE (2)
#define XSTATE_MASK_ARM64_SVE (UI64(1) << (XSTATE_ARM64_SVE))

//
// This flag is set by the unwinder if it has unwound to a call
// site, and cleared whenever it unwinds through a trap frame.
// It is used by language-specific exception handlers to help
// differentiate exception scopes during dispatching.
//

#define CONTEXT_UNWOUND_TO_CALL 0x20000000

//
// Define initial Cpsr/Fpscr value
//

#define INITIAL_CPSR 0x10
#define INITIAL_FPSCR 0

// begin_ntoshvp

//
// Specify the number of breakpoints and watchpoints that the OS
// will track. Architecturally, ARM64 supports up to 16. In practice,
// however, almost no one implements more than 4 of each.
//

#define ARM64_MAX_BREAKPOINTS     8
#define ARM64_MAX_WATCHPOINTS     2

typedef struct _NEON128 {
    ULONGLONG Low;
    LONGLONG High;
} NEON128, *PNEON128;

typedef struct DECLSPEC_ALIGN(16) _CONTEXT {

    //
    // Control flags.
    //

    /* +0x000 */ DWORD ContextFlags;

    //
    // Integer registers
    //

    /* +0x004 */ DWORD Cpsr;       // NZVF + DAIF + CurrentEL + SPSel
    /* +0x008 */ union {
                    struct {
                        DWORD64 X0;
                        DWORD64 X1;
                        DWORD64 X2;
                        DWORD64 X3;
                        DWORD64 X4;
                        DWORD64 X5;
                        DWORD64 X6;
                        DWORD64 X7;
                        DWORD64 X8;
                        DWORD64 X9;
                        DWORD64 X10;
                        DWORD64 X11;
                        DWORD64 X12;
                        DWORD64 X13;
                        DWORD64 X14;
                        DWORD64 X15;
                        DWORD64 X16;
                        DWORD64 X17;
                        DWORD64 X18;
                        DWORD64 X19;
                        DWORD64 X20;
                        DWORD64 X21;
                        DWORD64 X22;
                        DWORD64 X23;
                        DWORD64 X24;
                        DWORD64 X25;
                        DWORD64 X26;
                        DWORD64 X27;
                        DWORD64 X28;
                    };
                    DWORD64 X[29];
                };
    /* +0x0f0 */ DWORD64 Fp;
    /* +0x0f8 */ DWORD64 Lr;
    /* +0x100 */ DWORD64 Sp;
    /* +0x108 */ DWORD64 Pc;

    //
    // Floating Point/NEON Registers
    //

    /* +0x110 */ NEON128 V[32];
    /* +0x310 */ DWORD Fpcr;
    /* +0x314 */ DWORD Fpsr;

    //
    // Debug registers
    //

    /* +0x318 */ DWORD Bcr[ARM64_MAX_BREAKPOINTS];
    /* +0x338 */ DWORD64 Bvr[ARM64_MAX_BREAKPOINTS];
    /* +0x378 */ DWORD Wcr[ARM64_MAX_WATCHPOINTS];
    /* +0x380 */ DWORD64 Wvr[ARM64_MAX_WATCHPOINTS];

    /* +0x390 */ DWORD64 XStateFeaturesMask;

    //
    // Sve Registers
    //
    // TODO-SVE: Support Vector register sizes >128bit
    // For 128bit, Z and V registers fully overlap, so there is no need to load/store both.
    /* +0x398 */ DWORD Vl;
    /* +0x39c */ DWORD Ffr;
    /* +0x3a0 */ DWORD P[16];
    /* +0x3e0 */

} CONTEXT, *PCONTEXT, *LPCONTEXT;

//
// Nonvolatile context pointer record.
//

typedef struct _KNONVOLATILE_CONTEXT_POINTERS {

    PDWORD64 X19;
    PDWORD64 X20;
    PDWORD64 X21;
    PDWORD64 X22;
    PDWORD64 X23;
    PDWORD64 X24;
    PDWORD64 X25;
    PDWORD64 X26;
    PDWORD64 X27;
    PDWORD64 X28;
    PDWORD64 Fp;
    PDWORD64 Lr;

    PDWORD64 D8;
    PDWORD64 D9;
    PDWORD64 D10;
    PDWORD64 D11;
    PDWORD64 D12;
    PDWORD64 D13;
    PDWORD64 D14;
    PDWORD64 D15;

} KNONVOLATILE_CONTEXT_POINTERS, *PKNONVOLATILE_CONTEXT_POINTERS;

typedef struct _IMAGE_ARM64_RUNTIME_FUNCTION_ENTRY {
    DWORD BeginAddress;
    union {
        DWORD UnwindData;
        struct {
            DWORD Flag : 2;
            DWORD FunctionLength : 11;
            DWORD RegF : 3;
            DWORD RegI : 4;
            DWORD H : 1;
            DWORD CR : 2;
            DWORD FrameSize : 9;
        };
    };
} IMAGE_ARM64_RUNTIME_FUNCTION_ENTRY, * PIMAGE_ARM64_RUNTIME_FUNCTION_ENTRY;

typedef union IMAGE_ARM64_RUNTIME_FUNCTION_ENTRY_XDATA {
    ULONG HeaderData;
    struct {
        ULONG FunctionLength : 18;      // in words (2 bytes)
        ULONG Version : 2;
        ULONG ExceptionDataPresent : 1;
        ULONG EpilogInHeader : 1;
        ULONG EpilogCount : 5;          // number of epilogs or byte index of the first unwind code for the one only epilog
        ULONG CodeWords : 5;            // number of dwords with unwind codes
    };
} IMAGE_ARM64_RUNTIME_FUNCTION_ENTRY_XDATA;

#elif defined(HOST_LOONGARCH64)

// Please refer to src/coreclr/pal/src/arch/loongarch64/asmconstants.h
#define CONTEXT_LOONGARCH64   0x00800000

#define CONTEXT_CONTROL (CONTEXT_LOONGARCH64 | 0x1)
#define CONTEXT_INTEGER (CONTEXT_LOONGARCH64 | 0x2)
#define CONTEXT_FLOATING_POINT  (CONTEXT_LOONGARCH64 | 0x4)
#define CONTEXT_DEBUG_REGISTERS (CONTEXT_LOONGARCH64 | 0x8)
#define CONTEXT_FULL (CONTEXT_CONTROL | CONTEXT_INTEGER | CONTEXT_FLOATING_POINT)

#define CONTEXT_ALL (CONTEXT_CONTROL | CONTEXT_INTEGER | CONTEXT_FLOATING_POINT | CONTEXT_DEBUG_REGISTERS)

#define CONTEXT_EXCEPTION_ACTIVE 0x8000000
#define CONTEXT_SERVICE_ACTIVE 0x10000000
#define CONTEXT_EXCEPTION_REQUEST 0x40000000
#define CONTEXT_EXCEPTION_REPORTING 0x80000000

//
// This flag is set by the unwinder if it has unwound to a call
// site, and cleared whenever it unwinds through a trap frame.
// It is used by language-specific exception handlers to help
// differentiate exception scopes during dispatching.
//

#define CONTEXT_UNWOUND_TO_CALL 0x20000000

// begin_ntoshvp

//
// Specify the number of breakpoints and watchpoints that the OS
// will track. Architecturally, LOONGARCH64 supports up to 16. In practice,
// however, almost no one implements more than 4 of each.
//

#define LOONGARCH64_MAX_BREAKPOINTS     8
#define LOONGARCH64_MAX_WATCHPOINTS     2

typedef struct DECLSPEC_ALIGN(16) _CONTEXT {

    //
    // Control flags.
    //

    /* +0x000 */ DWORD ContextFlags;

    //
    // Integer registers.
    //
    DWORD64 R0;
    DWORD64 Ra;
    DWORD64 Tp;
    DWORD64 Sp;
    DWORD64 A0;//DWORD64 V0;
    DWORD64 A1;//DWORD64 V1;
    DWORD64 A2;
    DWORD64 A3;
    DWORD64 A4;
    DWORD64 A5;
    DWORD64 A6;
    DWORD64 A7;
    DWORD64 T0;
    DWORD64 T1;
    DWORD64 T2;
    DWORD64 T3;
    DWORD64 T4;
    DWORD64 T5;
    DWORD64 T6;
    DWORD64 T7;
    DWORD64 T8;
    DWORD64 X0;
    DWORD64 Fp;
    DWORD64 S0;
    DWORD64 S1;
    DWORD64 S2;
    DWORD64 S3;
    DWORD64 S4;
    DWORD64 S5;
    DWORD64 S6;
    DWORD64 S7;
    DWORD64 S8;
    DWORD64 Pc;

    //
    // Floating Point Registers: FPR64/LSX/LASX.
    //
    ULONGLONG F[4*32];
    DWORD64 Fcc;
    DWORD Fcsr;
} CONTEXT, *PCONTEXT, *LPCONTEXT;

//
// Nonvolatile context pointer record.
//

typedef struct _KNONVOLATILE_CONTEXT_POINTERS {

    PDWORD64 S0;
    PDWORD64 S1;
    PDWORD64 S2;
    PDWORD64 S3;
    PDWORD64 S4;
    PDWORD64 S5;
    PDWORD64 S6;
    PDWORD64 S7;
    PDWORD64 S8;
    PDWORD64 Fp;
    PDWORD64 Ra;

    PDWORD64 F24;
    PDWORD64 F25;
    PDWORD64 F26;
    PDWORD64 F27;
    PDWORD64 F28;
    PDWORD64 F29;
    PDWORD64 F30;
    PDWORD64 F31;
} KNONVOLATILE_CONTEXT_POINTERS, *PKNONVOLATILE_CONTEXT_POINTERS;

#elif defined(HOST_RISCV64)

// Please refer to src/coreclr/pal/src/arch/riscv64/asmconstants.h
#define CONTEXT_RISCV64 0x01000000L

#define CONTEXT_CONTROL (CONTEXT_RISCV64 | 0x1)
#define CONTEXT_INTEGER (CONTEXT_RISCV64 | 0x2)
#define CONTEXT_FLOATING_POINT  (CONTEXT_RISCV64 | 0x4)
#define CONTEXT_DEBUG_REGISTERS (CONTEXT_RISCV64 | 0x8)

#define CONTEXT_FULL (CONTEXT_CONTROL | CONTEXT_INTEGER | CONTEXT_FLOATING_POINT)

#define CONTEXT_ALL (CONTEXT_CONTROL | CONTEXT_INTEGER | CONTEXT_FLOATING_POINT | CONTEXT_DEBUG_REGISTERS)

#define CONTEXT_EXCEPTION_ACTIVE 0x8000000
#define CONTEXT_SERVICE_ACTIVE 0x10000000
#define CONTEXT_EXCEPTION_REQUEST 0x40000000
#define CONTEXT_EXCEPTION_REPORTING 0x80000000

//
// This flag is set by the unwinder if it has unwound to a call
// site, and cleared whenever it unwinds through a trap frame.
// It is used by language-specific exception handlers to help
// differentiate exception scopes during dispatching.
//

#define CONTEXT_UNWOUND_TO_CALL 0x20000000

// begin_ntoshvp

//
// Specify the number of breakpoints and watchpoints that the OS
// will track. Architecturally, RISCV64 supports up to 16. In practice,
// however, almost no one implements more than 4 of each.
//

#define RISCV64_MAX_BREAKPOINTS     8
#define RISCV64_MAX_WATCHPOINTS     2

typedef struct DECLSPEC_ALIGN(16) _CONTEXT {

    //
    // Control flags.
    //

    /* +0x000 */ DWORD ContextFlags;

    //
    // Integer registers.
    //
    DWORD64 R0;
    DWORD64 Ra;
    DWORD64 Sp;
    DWORD64 Gp;
    DWORD64 Tp;
    DWORD64 T0;
    DWORD64 T1;
    DWORD64 T2;
    DWORD64 Fp;
    DWORD64 S1;
    DWORD64 A0;
    DWORD64 A1;
    DWORD64 A2;
    DWORD64 A3;
    DWORD64 A4;
    DWORD64 A5;
    DWORD64 A6;
    DWORD64 A7;
    DWORD64 S2;
    DWORD64 S3;
    DWORD64 S4;
    DWORD64 S5;
    DWORD64 S6;
    DWORD64 S7;
    DWORD64 S8;
    DWORD64 S9;
    DWORD64 S10;
    DWORD64 S11;
    DWORD64 T3;
    DWORD64 T4;
    DWORD64 T5;
    DWORD64 T6;
    DWORD64 Pc;

    //
    // Floating Point Registers
    //
    // TODO-RISCV64: support the SIMD.
    ULONGLONG F[32];
    DWORD Fcsr;
} CONTEXT, *PCONTEXT, *LPCONTEXT;

//
// Nonvolatile context pointer record.
//

typedef struct _KNONVOLATILE_CONTEXT_POINTERS {

    PDWORD64 S1;
    PDWORD64 S2;
    PDWORD64 S3;
    PDWORD64 S4;
    PDWORD64 S5;
    PDWORD64 S6;
    PDWORD64 S7;
    PDWORD64 S8;
    PDWORD64 S9;
    PDWORD64 S10;
    PDWORD64 S11;
    PDWORD64 Fp;
    PDWORD64 Gp;
    PDWORD64 Tp;
    PDWORD64 Ra;

    PDWORD64 F8;
    PDWORD64 F9;
    PDWORD64 F18;
    PDWORD64 F19;
    PDWORD64 F20;
    PDWORD64 F21;
    PDWORD64 F22;
    PDWORD64 F23;
    PDWORD64 F24;
    PDWORD64 F25;
    PDWORD64 F26;
    PDWORD64 F27;
} KNONVOLATILE_CONTEXT_POINTERS, *PKNONVOLATILE_CONTEXT_POINTERS;

#elif defined(HOST_S390X)

// There is no context for s390x defined in winnt.h,
// so we re-use the amd64 values.
#define CONTEXT_S390X   0x100000

#define CONTEXT_CONTROL (CONTEXT_S390X | 0x1L)
#define CONTEXT_INTEGER (CONTEXT_S390X | 0x2L)
#define CONTEXT_FLOATING_POINT  (CONTEXT_S390X | 0x4L)

#define CONTEXT_FULL (CONTEXT_CONTROL | CONTEXT_INTEGER | CONTEXT_FLOATING_POINT)

#define CONTEXT_ALL (CONTEXT_CONTROL | CONTEXT_INTEGER | CONTEXT_FLOATING_POINT)

#define CONTEXT_EXCEPTION_ACTIVE 0x8000000
#define CONTEXT_SERVICE_ACTIVE 0x10000000
#define CONTEXT_EXCEPTION_REQUEST 0x40000000
#define CONTEXT_EXCEPTION_REPORTING 0x80000000

typedef struct DECLSPEC_ALIGN(8) _CONTEXT {

    //
    // Control flags.
    //

    DWORD ContextFlags;

    //
    // Integer registers.
    //

    union {
        DWORD64 Gpr[16];
        struct {
            DWORD64 R0;
            DWORD64 R1;
            DWORD64 R2;
            DWORD64 R3;
            DWORD64 R4;
            DWORD64 R5;
            DWORD64 R6;
            DWORD64 R7;
            DWORD64 R8;
            DWORD64 R9;
            DWORD64 R10;
            DWORD64 R11;
            DWORD64 R12;
            DWORD64 R13;
            DWORD64 R14;
            DWORD64 R15;
        };
    };

    //
    // Floating-point registers.
    //

    union {
        DWORD64 Fpr[16];
        struct {
            DWORD64 F0;
            DWORD64 F1;
            DWORD64 F2;
            DWORD64 F3;
            DWORD64 F4;
            DWORD64 F5;
            DWORD64 F6;
            DWORD64 F7;
            DWORD64 F8;
            DWORD64 F9;
            DWORD64 F10;
            DWORD64 F11;
            DWORD64 F12;
            DWORD64 F13;
            DWORD64 F14;
            DWORD64 F15;
        };
    };

    //
    // Control registers.
    //

    DWORD64 PSWMask;
    DWORD64 PSWAddr;

} CONTEXT, *PCONTEXT, *LPCONTEXT;

//
// Nonvolatile context pointer record.
//

typedef struct _KNONVOLATILE_CONTEXT_POINTERS {
    PDWORD64 R6;
    PDWORD64 R7;
    PDWORD64 R8;
    PDWORD64 R9;
    PDWORD64 R10;
    PDWORD64 R11;
    PDWORD64 R12;
    PDWORD64 R13;
    PDWORD64 R14;
    PDWORD64 R15;

} KNONVOLATILE_CONTEXT_POINTERS, *PKNONVOLATILE_CONTEXT_POINTERS;

#elif defined(HOST_POWERPC64)

// There is no context for ppc64le defined in winnt.h,
// so we re-use the amd64 values.
#define CONTEXT_PPC64   0x100000

#define CONTEXT_CONTROL (CONTEXT_PPC64 | 0x1L)
#define CONTEXT_INTEGER (CONTEXT_PPC64 | 0x2L)
#define CONTEXT_FLOATING_POINT  (CONTEXT_PPC64 | 0x4L)

#define CONTEXT_FULL (CONTEXT_CONTROL | CONTEXT_INTEGER | CONTEXT_FLOATING_POINT)

#define CONTEXT_ALL (CONTEXT_CONTROL | CONTEXT_INTEGER | CONTEXT_FLOATING_POINT)

#define CONTEXT_EXCEPTION_ACTIVE 0x8000000
#define CONTEXT_SERVICE_ACTIVE 0x10000000
#define CONTEXT_EXCEPTION_REQUEST 0x40000000
#define CONTEXT_EXCEPTION_REPORTING 0x80000000

typedef struct DECLSPEC_ALIGN(16) _CONTEXT {

    //
    // Control flags.
    //

    DWORD ContextFlags;

    //
    // Integer  Registers
    //

    DWORD64 R0;
    DWORD64 R1;
    DWORD64 R2;
    DWORD64 R3;
    DWORD64 R4;
    DWORD64 R5;
    DWORD64 R6;
    DWORD64 R7;
    DWORD64 R8;
    DWORD64 R9;
    DWORD64 R10;
    DWORD64 R11;
    DWORD64 R12;
    DWORD64 R13;
    DWORD64 R14;
    DWORD64 R15;
    DWORD64 R16;
    DWORD64 R17;
    DWORD64 R18;
    DWORD64 R19;
    DWORD64 R20;
    DWORD64 R21;
    DWORD64 R22;
    DWORD64 R23;
    DWORD64 R24;
    DWORD64 R25;
    DWORD64 R26;
    DWORD64 R27;
    DWORD64 R28;
    DWORD64 R29;
    DWORD64 R30;
    DWORD64 R31;

    //
    // Floaring Point Registers
    //

    DWORD64 F0;
    DWORD64 F1;
    DWORD64 F2;
    DWORD64 F3;
    DWORD64 F4;
    DWORD64 F5;
    DWORD64 F6;
    DWORD64 F7;
    DWORD64 F8;
    DWORD64 F9;
    DWORD64 F10;
    DWORD64 F11;
    DWORD64 F12;
    DWORD64 F13;
    DWORD64 F14;
    DWORD64 F15;
    DWORD64 F16;
    DWORD64 F17;
    DWORD64 F18;
    DWORD64 F19;
    DWORD64 F20;
    DWORD64 F21;
    DWORD64 F22;
    DWORD64 F23;
    DWORD64 F24;
    DWORD64 F25;
    DWORD64 F26;
    DWORD64 F27;
    DWORD64 F28;
    DWORD64 F29;
    DWORD64 F30;
    DWORD64 F31;
    DWORD64 Fpscr;

    //
    // Control Registers
    //

    DWORD64 Nip;
    DWORD64 Msr;
    DWORD64 Ctr;
    DWORD64 Link;

    DWORD Xer;
    DWORD Ccr;


} CONTEXT, *PCONTEXT, *LPCONTEXT;

//
// Nonvolatile context pointer record.
//

typedef struct _KNONVOLATILE_CONTEXT_POINTERS {
    PDWORD64 R14;
    PDWORD64 R15;
    PDWORD64 R16;
    PDWORD64 R17;
    PDWORD64 R18;
    PDWORD64 R19;
    PDWORD64 R20;
    PDWORD64 R21;
    PDWORD64 R22;
    PDWORD64 R23;
    PDWORD64 R24;
    PDWORD64 R25;
    PDWORD64 R26;
    PDWORD64 R27;
    PDWORD64 R28;
    PDWORD64 R29;
    PDWORD64 R30;
    PDWORD64 R31;

    //
    // Need to add Floating point non-volatile registers.
    //

} KNONVOLATILE_CONTEXT_POINTERS, *PKNONVOLATILE_CONTEXT_POINTERS;
#elif defined(HOST_WASM)
#define CONTEXT_CONTROL 0
#define CONTEXT_INTEGER 0
#define CONTEXT_FLOATING_POINT 0
#define CONTEXT_DEBUG_REGISTERS 0
#define CONTEXT_FULL 0
#define CONTEXT_ALL 0

#define CONTEXT_XSTATE 0

#define CONTEXT_EXCEPTION_ACTIVE 0x8000000L
#define CONTEXT_SERVICE_ACTIVE 0x10000000L
#define CONTEXT_EXCEPTION_REQUEST 0x40000000L
#define CONTEXT_EXCEPTION_REPORTING 0x80000000L

typedef struct _CONTEXT {
    ULONG ContextFlags;
} CONTEXT, *PCONTEXT, *LPCONTEXT;

typedef struct _KNONVOLATILE_CONTEXT_POINTERS {
    DWORD none;
} KNONVOLATILE_CONTEXT_POINTERS, *PKNONVOLATILE_CONTEXT_POINTERS;

#else
#error Unknown architecture for defining CONTEXT.
#endif


PALIMPORT
BOOL
PALAPI
GetThreadContext(
         IN HANDLE hThread,
         IN OUT LPCONTEXT lpContext);

PALIMPORT
BOOL
PALAPI
SetThreadContext(
         IN HANDLE hThread,
         IN CONST CONTEXT *lpContext);

#define THREAD_BASE_PRIORITY_LOWRT    15
#define THREAD_BASE_PRIORITY_MAX      2
#define THREAD_BASE_PRIORITY_MIN      (-2)
#define THREAD_BASE_PRIORITY_IDLE     (-15)

#define THREAD_PRIORITY_LOWEST        THREAD_BASE_PRIORITY_MIN
#define THREAD_PRIORITY_BELOW_NORMAL  (THREAD_PRIORITY_LOWEST+1)
#define THREAD_PRIORITY_NORMAL        0
#define THREAD_PRIORITY_HIGHEST       THREAD_BASE_PRIORITY_MAX
#define THREAD_PRIORITY_ABOVE_NORMAL  (THREAD_PRIORITY_HIGHEST-1)
#define THREAD_PRIORITY_ERROR_RETURN  (MAXLONG)

#define THREAD_PRIORITY_TIME_CRITICAL THREAD_BASE_PRIORITY_LOWRT
#define THREAD_PRIORITY_IDLE          THREAD_BASE_PRIORITY_IDLE

PALIMPORT
int
PALAPI
GetThreadPriority(
          IN HANDLE hThread);

PALIMPORT
BOOL
PALAPI
SetThreadPriority(
          IN HANDLE hThread,
          IN int nPriority);

PALIMPORT
HRESULT
PALAPI
SetThreadDescription(
    IN HANDLE hThread,
    IN PCWSTR lpThreadDescription
);

#define TLS_OUT_OF_INDEXES ((DWORD)0xFFFFFFFF)

PALIMPORT
PVOID
PALAPI
PAL_GetStackBase();

PALIMPORT
PVOID
PALAPI
PAL_GetStackLimit();

PALIMPORT
DWORD
PALAPI
PAL_GetLogicalCpuCountFromOS();

PALIMPORT
DWORD
PALAPI
PAL_GetTotalCpuCount();

PALIMPORT
BOOL
PALAPI
PAL_GetCpuLimit(UINT* val);

typedef BOOL(*UnwindReadMemoryCallback)(PVOID address, PVOID buffer, SIZE_T size);

PALIMPORT BOOL PALAPI PAL_VirtualUnwind(CONTEXT *context, KNONVOLATILE_CONTEXT_POINTERS *contextPointers);

PALIMPORT BOOL PALAPI PAL_VirtualUnwindOutOfProc(CONTEXT *context, PULONG64 functionStart, SIZE_T baseAddress, UnwindReadMemoryCallback readMemoryCallback);

PALIMPORT BOOL PALAPI PAL_GetUnwindInfoSize(SIZE_T baseAddress, ULONG64 ehFrameHdrAddr, UnwindReadMemoryCallback readMemoryCallback, PULONG64 ehFrameStart, PULONG64 ehFrameSize);

#define PAGE_NOACCESS                   0x01
#define PAGE_READONLY                   0x02
#define PAGE_READWRITE                  0x04
#define PAGE_WRITECOPY                  0x08
#define PAGE_EXECUTE                    0x10
#define PAGE_EXECUTE_READ               0x20
#define PAGE_EXECUTE_READWRITE          0x40
#define PAGE_EXECUTE_WRITECOPY          0x80
#define MEM_COMMIT                      0x1000
#define MEM_RESERVE                     0x2000
#define MEM_DECOMMIT                    0x4000
#define MEM_RELEASE                     0x8000
#define MEM_FREE                        0x10000
#define MEM_PRIVATE                     0x20000
#define MEM_MAPPED                      0x40000
#define MEM_TOP_DOWN                    0x100000
#define MEM_WRITE_WATCH                 0x200000
#define MEM_LARGE_PAGES                 0x20000000
#define MEM_RESERVE_EXECUTABLE          0x40000000 // reserve memory using executable memory allocator

PALIMPORT
HANDLE
PALAPI
CreateFileMappingW(
           IN HANDLE hFile,
           IN LPSECURITY_ATTRIBUTES lpFileMappingAttributes,
           IN DWORD flProtect,
           IN DWORD dwMaxmimumSizeHigh,
           IN DWORD dwMaximumSizeLow,
           IN LPCWSTR lpName);

#define CreateFileMapping CreateFileMappingW

#define SECTION_QUERY       0x0001
#define SECTION_MAP_WRITE   0x0002
#define SECTION_MAP_READ    0x0004
#define SECTION_ALL_ACCESS  (SECTION_MAP_READ | SECTION_MAP_WRITE) // diff from winnt.h

#define FILE_MAP_WRITE      SECTION_MAP_WRITE
#define FILE_MAP_READ       SECTION_MAP_READ
#define FILE_MAP_ALL_ACCESS SECTION_ALL_ACCESS
#define FILE_MAP_COPY       SECTION_QUERY

typedef INT_PTR (PALAPI_NOEXPORT *FARPROC)();

PALIMPORT
LPVOID
PALAPI
MapViewOfFile(
          IN HANDLE hFileMappingObject,
          IN DWORD dwDesiredAccess,
          IN DWORD dwFileOffsetHigh,
          IN DWORD dwFileOffsetLow,
          IN SIZE_T dwNumberOfBytesToMap);

PALIMPORT
LPVOID
PALAPI
MapViewOfFileEx(
          IN HANDLE hFileMappingObject,
          IN DWORD dwDesiredAccess,
          IN DWORD dwFileOffsetHigh,
          IN DWORD dwFileOffsetLow,
          IN SIZE_T dwNumberOfBytesToMap,
          IN LPVOID lpBaseAddress);

PALIMPORT
BOOL
PALAPI
UnmapViewOfFile(
        IN LPCVOID lpBaseAddress);

PALIMPORT
HMODULE
PALAPI
LoadLibraryExW(
        IN LPCWSTR lpLibFileName,
        IN /*Reserved*/ HANDLE hFile = NULL,
        IN DWORD dwFlags = 0);

PALIMPORT
NATIVE_LIBRARY_HANDLE
PALAPI
PAL_LoadLibraryDirect(
        IN LPCWSTR lpLibFileName);

PALIMPORT
BOOL
PALAPI
PAL_FreeLibraryDirect(
        IN NATIVE_LIBRARY_HANDLE dl_handle);

PALIMPORT
HMODULE
PALAPI
PAL_GetPalHostModule();

PALIMPORT
FARPROC
PALAPI
PAL_GetProcAddressDirect(
        IN NATIVE_LIBRARY_HANDLE dl_handle,
        IN LPCSTR lpProcName);

/*++
Function:
  PAL_LOADLoadPEFile

Abstract
  Loads a PE file into memory.  Properly maps all of the sections in the PE file.  Returns a pointer to the
  loaded base.

Parameters:
    IN hFile    - The file to load
    IN offset - offset within hFile where the PE "file" is located

Return value:
    A valid base address if successful.
    0 if failure
--*/
PALIMPORT
PVOID
PALAPI
PAL_LOADLoadPEFile(HANDLE hFile, size_t offset);

/*++
    PAL_LOADUnloadPEFile

    Unload a PE file that was loaded by PAL_LOADLoadPEFile().

Parameters:
    IN ptr - the file pointer returned by PAL_LOADLoadPEFile()

Return value:
    TRUE - success
    FALSE - failure (incorrect ptr, etc.)
--*/
PALIMPORT
BOOL
PALAPI
PAL_LOADUnloadPEFile(PVOID ptr);

/*++
    PAL_LOADMarkSectionAsNotNeeded

    Mark a section as NotNeeded that was loaded by PAL_LOADLoadPEFile().

Parameters:
    IN ptr - the section address mapped by PAL_LOADLoadPEFile()

Return value:
    TRUE - success
    FALSE - failure (incorrect ptr, etc.)
--*/
BOOL
PALAPI
PAL_LOADMarkSectionAsNotNeeded(void * ptr);

PALIMPORT
FARPROC
PALAPI
GetProcAddress(
    IN HMODULE hModule,
    IN LPCSTR lpProcName);

PALIMPORT
BOOL
PALAPI
FreeLibrary(
    IN OUT HMODULE hLibModule);

PALIMPORT
BOOL
PALAPI
DisableThreadLibraryCalls(
    IN HMODULE hLibModule);

PALIMPORT
DWORD
PALAPI
GetModuleFileNameW(
    IN HMODULE hModule,
    OUT LPWSTR lpFileName,
    IN DWORD nSize);

#ifdef UNICODE
#define GetModuleFileName GetModuleFileNameW
#else
#define GetModuleFileName GetModuleFileNameA
#endif

// Get base address of the module containing a given symbol
PALIMPORT
LPCVOID
PALAPI
PAL_GetSymbolModuleBase(PVOID symbol);

PALIMPORT
int
PALAPI
PAL_CopyModuleData(PVOID moduleBase, PVOID destinationBufferStart, PVOID destinationBufferEnd);;

PALIMPORT
LPCSTR
PALAPI
PAL_GetLoadLibraryError();

PALIMPORT
LPVOID
PALAPI
PAL_VirtualReserveFromExecutableMemoryAllocatorWithinRange(
    IN LPCVOID lpBeginAddress,
    IN LPCVOID lpEndAddress,
    IN SIZE_T dwSize,
    IN BOOL storeAllocationInfo);

PALIMPORT
void
PALAPI
PAL_GetExecutableMemoryAllocatorPreferredRange(
    OUT PVOID *start,
    OUT PVOID *end);

PALIMPORT
LPVOID
PALAPI
VirtualAlloc(
         IN LPVOID lpAddress,
         IN SIZE_T dwSize,
         IN DWORD flAllocationType,
         IN DWORD flProtect);

PALIMPORT
BOOL
PALAPI
VirtualFree(
        IN LPVOID lpAddress,
        IN SIZE_T dwSize,
        IN DWORD dwFreeType);


#if defined(HOST_APPLE) && defined(HOST_ARM64)

PALIMPORT
VOID
PALAPI
PAL_JitWriteProtect(bool writeEnable);

#endif // defined(HOST_APPLE) && defined(HOST_ARM64)


PALIMPORT
BOOL
PALAPI
VirtualProtect(
           IN LPVOID lpAddress,
           IN SIZE_T dwSize,
           IN DWORD flNewProtect,
           OUT PDWORD lpflOldProtect);

typedef struct _MEMORY_BASIC_INFORMATION {
    PVOID BaseAddress;
    PVOID AllocationBase_PAL_Undefined;
    DWORD AllocationProtect;
    SIZE_T RegionSize;
    DWORD State;
    DWORD Protect;
    DWORD Type;
} MEMORY_BASIC_INFORMATION, *PMEMORY_BASIC_INFORMATION;

PALIMPORT
SIZE_T
PALAPI
VirtualQuery(
         IN LPCVOID lpAddress,
         OUT PMEMORY_BASIC_INFORMATION lpBuffer,
         IN SIZE_T dwLength);

#define MoveMemory memmove
#define CopyMemory memcpy
#define FillMemory(Destination,Length,Fill) memset((Destination),(Fill),(Length))
#define ZeroMemory(Destination,Length) memset((Destination),0,(Length))

PALIMPORT
BOOL
PALAPI
FlushInstructionCache(
              IN HANDLE hProcess,
              IN LPCVOID lpBaseAddress,
              IN SIZE_T dwSize);

#define MAX_LEADBYTES         12
#define MAX_DEFAULTCHAR       2

typedef struct _cpinfo {
    UINT MaxCharSize;
    BYTE DefaultChar[MAX_DEFAULTCHAR];
    BYTE LeadByte[MAX_LEADBYTES];
} CPINFO, *LPCPINFO;

#define MB_PRECOMPOSED            0x00000001
#define MB_ERR_INVALID_CHARS      0x00000008

PALIMPORT
int
PALAPI
MultiByteToWideChar(
            IN UINT CodePage,
            IN DWORD dwFlags,
            IN LPCSTR lpMultiByteStr,
            IN int cbMultiByte,
            OUT LPWSTR lpWideCharStr,
            IN int cchWideChar);

#define WC_NO_BEST_FIT_CHARS      0x00000400

PALIMPORT
int
PALAPI
WideCharToMultiByte(
            IN UINT CodePage,
            IN DWORD dwFlags,
            IN LPCWSTR lpWideCharStr,
            IN int cchWideChar,
            OUT LPSTR lpMultiByteStr,
            IN int cbMultyByte,
            IN LPCSTR lpDefaultChar,
            OUT LPBOOL lpUsedDefaultChar);

#define EXCEPTION_NONCONTINUABLE 0x1
#define EXCEPTION_UNWINDING 0x2

#define EXCEPTION_EXIT_UNWIND 0x4       // Exit unwind is in progress (not used by PAL SEH)
#define EXCEPTION_NESTED_CALL 0x10      // Nested exception handler call
#define EXCEPTION_TARGET_UNWIND 0x20    // Target unwind in progress
#define EXCEPTION_COLLIDED_UNWIND 0x40  // Collided exception handler call
#define EXCEPTION_SKIP_VEH 0x200

#define EXCEPTION_UNWIND (EXCEPTION_UNWINDING | EXCEPTION_EXIT_UNWIND | \
                          EXCEPTION_TARGET_UNWIND | EXCEPTION_COLLIDED_UNWIND)

#define IS_DISPATCHING(Flag) ((Flag & EXCEPTION_UNWIND) == 0)
#define IS_UNWINDING(Flag) ((Flag & EXCEPTION_UNWIND) != 0)
#define IS_TARGET_UNWIND(Flag) (Flag & EXCEPTION_TARGET_UNWIND)

#define EXCEPTION_IS_SIGNAL 0x100

#define EXCEPTION_MAXIMUM_PARAMETERS 15

// Index in the ExceptionInformation array where we will keep the reference
// to the native exception that needs to be deleted when dispatching
// exception in managed code.
#define NATIVE_EXCEPTION_ASYNC_SLOT (EXCEPTION_MAXIMUM_PARAMETERS-1)

typedef struct _EXCEPTION_RECORD {
    DWORD ExceptionCode;
    DWORD ExceptionFlags;
    struct _EXCEPTION_RECORD *ExceptionRecord;
    PVOID ExceptionAddress;
    DWORD NumberParameters;
    ULONG_PTR ExceptionInformation[EXCEPTION_MAXIMUM_PARAMETERS];
} EXCEPTION_RECORD, *PEXCEPTION_RECORD;

typedef struct _EXCEPTION_POINTERS {
    PEXCEPTION_RECORD ExceptionRecord;
    PCONTEXT ContextRecord;
} EXCEPTION_POINTERS, *PEXCEPTION_POINTERS, *LPEXCEPTION_POINTERS;

typedef LONG EXCEPTION_DISPOSITION;

enum {
    ExceptionContinueExecution,
    ExceptionContinueSearch,
    ExceptionNestedException,
    ExceptionCollidedUnwind,
};

//
// A function table entry is generated for each frame function.
//
#if defined(HOST_ARM64)
typedef IMAGE_ARM64_RUNTIME_FUNCTION_ENTRY RUNTIME_FUNCTION, *PRUNTIME_FUNCTION;
#else // HOST_ARM64
typedef struct _RUNTIME_FUNCTION {
    DWORD BeginAddress;
#ifdef HOST_AMD64
    DWORD EndAddress;
#endif
    DWORD UnwindData;
} RUNTIME_FUNCTION, *PRUNTIME_FUNCTION;
#endif // HOST_ARM64

#define STANDARD_RIGHTS_REQUIRED  (0x000F0000L)
#define SYNCHRONIZE               (0x00100000L)
#define READ_CONTROL              (0x00020000L)
#define MAXIMUM_ALLOWED           (0x02000000L)

#define EVENT_MODIFY_STATE        (0x0002)
#define EVENT_ALL_ACCESS          (STANDARD_RIGHTS_REQUIRED | SYNCHRONIZE | 0x3)

#define MUTANT_QUERY_STATE        (0x0001)
#define MUTANT_ALL_ACCESS         (STANDARD_RIGHTS_REQUIRED | SYNCHRONIZE | MUTANT_QUERY_STATE)
#define MUTEX_ALL_ACCESS          MUTANT_ALL_ACCESS

#define SEMAPHORE_MODIFY_STATE    (0x0002)
#define SEMAPHORE_ALL_ACCESS      (STANDARD_RIGHTS_REQUIRED | SYNCHRONIZE | 0x3)

#define PROCESS_TERMINATE         (0x0001)
#define PROCESS_CREATE_THREAD     (0x0002)
#define PROCESS_SET_SESSIONID     (0x0004)
#define PROCESS_VM_OPERATION      (0x0008)
#define PROCESS_VM_READ           (0x0010)
#define PROCESS_VM_WRITE          (0x0020)
#define PROCESS_DUP_HANDLE        (0x0040)
#define PROCESS_CREATE_PROCESS    (0x0080)
#define PROCESS_SET_QUOTA         (0x0100)
#define PROCESS_SET_INFORMATION   (0x0200)
#define PROCESS_QUERY_INFORMATION (0x0400)
#define PROCESS_SUSPEND_RESUME    (0x0800)
#define PROCESS_ALL_ACCESS        (STANDARD_RIGHTS_REQUIRED | SYNCHRONIZE | \
                                   0xFFF)

PALIMPORT
HANDLE
PALAPI
OpenProcess(
    IN DWORD dwDesiredAccess, /* PROCESS_DUP_HANDLE or PROCESS_ALL_ACCESS */
    IN BOOL bInheritHandle,
    IN DWORD dwProcessId
    );

PALIMPORT
VOID
PALAPI
OutputDebugStringA(
    IN LPCSTR lpOutputString);

PALIMPORT
VOID
PALAPI
OutputDebugStringW(
    IN LPCWSTR lpOutputStrig);

#ifdef UNICODE
#define OutputDebugString OutputDebugStringW
#else
#define OutputDebugString OutputDebugStringA
#endif

PALIMPORT
VOID
PALAPI
DebugBreak();

PALIMPORT
DWORD
PALAPI
GetEnvironmentVariableW(
            IN LPCWSTR lpName,
            OUT LPWSTR lpBuffer,
            IN DWORD nSize);

#ifdef UNICODE
#define GetEnvironmentVariable GetEnvironmentVariableW
#else
#define GetEnvironmentVariable GetEnvironmentVariableA
#endif

PALIMPORT
BOOL
PALAPI
SetEnvironmentVariableW(
            IN LPCWSTR lpName,
            IN LPCWSTR lpValue);

#ifdef UNICODE
#define SetEnvironmentVariable SetEnvironmentVariableW
#else
#define SetEnvironmentVariable SetEnvironmentVariableA
#endif

PALIMPORT
LPWSTR
PALAPI
GetEnvironmentStringsW();

#define GetEnvironmentStrings GetEnvironmentStringsW

PALIMPORT
BOOL
PALAPI
FreeEnvironmentStringsW(
            IN LPWSTR);

#define FreeEnvironmentStrings FreeEnvironmentStringsW

PALIMPORT
BOOL
PALAPI
CloseHandle(
        IN OUT HANDLE hObject);

PALIMPORT
VOID
PALAPI
RaiseException(
           IN DWORD dwExceptionCode,
           IN DWORD dwExceptionFlags,
           IN DWORD nNumberOfArguments,
           IN CONST ULONG_PTR *lpArguments);

PALIMPORT
VOID
PALAPI
DECLSPEC_NORETURN
RaiseFailFastException(
    IN PEXCEPTION_RECORD pExceptionRecord,
    IN PCONTEXT pContextRecord,
    IN DWORD dwFlags);

PALIMPORT
BOOL
PALAPI
QueryThreadCycleTime(
    IN HANDLE ThreadHandle,
    OUT PULONG64 CycleTime);

PALIMPORT
INT
PALAPI
PAL_nanosleep(
    IN long timeInNs);

typedef EXCEPTION_DISPOSITION (PALAPI_NOEXPORT *PVECTORED_EXCEPTION_HANDLER)(
                           struct _EXCEPTION_POINTERS *ExceptionPointers);

// Define BitScanForward64 and BitScanForward
// Per MSDN, BitScanForward64 will search the mask data from LSB to MSB for a set bit.
// If one is found, its bit position is stored in the out PDWORD argument and 1 is returned;
// otherwise, an undefined value is stored in the out PDWORD argument and 0 is returned.
//
// On GCC, the equivalent function is __builtin_ffsll. It returns 1+index of the least
// significant set bit, or 0 if if mask is zero.
//
// The same is true for BitScanForward, except that the GCC function is __builtin_ffs.
EXTERN_C
PALIMPORT
inline
unsigned char
PALAPI
BitScanForward(
    IN OUT PDWORD Index,
    IN UINT qwMask)
{
    int iIndex = __builtin_ffs(qwMask);
    // Set the Index after deducting unity
    *Index = (DWORD)(iIndex - 1);
    // Both GCC and Clang generate better, smaller code if we check whether the
    // mask was/is zero rather than the equivalent check that iIndex is zero.
    return qwMask != 0 ? TRUE : FALSE;
}

EXTERN_C
PALIMPORT
inline
unsigned char
PALAPI
BitScanForward64(
    IN OUT PDWORD Index,
    IN UINT64 qwMask)
{
    int iIndex = __builtin_ffsll(qwMask);
    // Set the Index after deducting unity
    *Index = (DWORD)(iIndex - 1);
    // Both GCC and Clang generate better, smaller code if we check whether the
    // mask was/is zero rather than the equivalent check that iIndex is zero.
    return qwMask != 0 ? TRUE : FALSE;
}

// Define BitScanReverse64 and BitScanReverse
// Per MSDN, BitScanReverse64 will search the mask data from MSB to LSB for a set bit.
// If one is found, its bit position is stored in the out PDWORD argument and 1 is returned.
// Otherwise, an undefined value is stored in the out PDWORD argument and 0 is returned.
//
// GCC/clang don't have a directly equivalent intrinsic; they do provide the __builtin_clzll
// intrinsic, which returns the number of leading 0-bits in x starting at the most significant
// bit position (the result is undefined when x = 0).
//
// The same is true for BitScanReverse, except that the GCC function is __builtin_clz.

EXTERN_C
PALIMPORT
inline
unsigned char
PALAPI
BitScanReverse(
    IN OUT PDWORD Index,
    IN UINT qwMask)
{
    // The result of __builtin_clz is undefined when qwMask is zero,
    // but it's still OK to call the intrinsic in that case (just don't use the output).
    // Unconditionally calling the intrinsic in this way allows the compiler to
    // emit branchless code for this function when possible (depending on how the
    // intrinsic is implemented for the target platform).
    int lzcount = __builtin_clz(qwMask);
    *Index = (DWORD)(31 - lzcount);
    return qwMask != 0;
}

EXTERN_C
PALIMPORT
inline
unsigned char
PALAPI
BitScanReverse64(
    IN OUT PDWORD Index,
    IN UINT64 qwMask)
{
    // The result of __builtin_clzll is undefined when qwMask is zero,
    // but it's still OK to call the intrinsic in that case (just don't use the output).
    // Unconditionally calling the intrinsic in this way allows the compiler to
    // emit branchless code for this function when possible (depending on how the
    // intrinsic is implemented for the target platform).
    int lzcount = __builtin_clzll(qwMask);
    *Index = (DWORD)(63 - lzcount);
    return qwMask != 0;
}

FORCEINLINE void PAL_InterlockedOperationBarrier()
{
#if (defined(HOST_ARM64) && !defined(LSE_INSTRUCTIONS_ENABLED_BY_DEFAULT) && !defined(__clang__)) || defined(HOST_LOONGARCH64) || defined(HOST_RISCV64)
    // On arm64, most of the __sync* functions generate a code sequence like:
    //   loop:
    //     ldaxr (load acquire exclusive)
    //     ...
    //     stlxr (store release exclusive)
    //     cbnz loop
    //
    // It is possible for a load following the code sequence above to be reordered to occur prior to the store above due to the
    // release barrier, this is substantiated by https://github.com/dotnet/coreclr/pull/17508. Interlocked operations in the PAL
    // require the load to occur after the store. This memory barrier should be used following a call to a __sync* function to
    // prevent that reordering. Code generated for arm32 includes a 'dmb' after 'cbnz', so no issue there at the moment.
    __sync_synchronize();
#endif
}

#if defined(HOST_ARM64)

#if defined(LSE_INSTRUCTIONS_ENABLED_BY_DEFAULT)

#define Define_InterlockMethod(RETURN_TYPE, METHOD_DECL, METHOD_INVOC, INTRINSIC_NAME) \
EXTERN_C PALIMPORT inline RETURN_TYPE PALAPI METHOD_DECL        \
{                                                               \
    return INTRINSIC_NAME;                                      \
}                                                               \

#else   // !LSE_INSTRUCTIONS_ENABLED_BY_DEFAULT

#define Define_InterlockMethod(RETURN_TYPE, METHOD_DECL, METHOD_INVOC, INTRINSIC_NAME) \
/* Function multiversioning will never inline a method that is  \
   marked such. However, just to make sure that we don't see    \
   surprises, explicitely mark them as noinline. */             \
EXTERN_C PALIMPORT inline RETURN_TYPE PALAPI                    \
__attribute__((target("+lse")))  __attribute__((noinline))      \
Lse_##METHOD_DECL                                               \
{                                                               \
    return INTRINSIC_NAME;                                      \
}                                                               \
                                                                \
EXTERN_C PALIMPORT inline RETURN_TYPE PALAPI METHOD_DECL        \
{                                                               \
    if (g_arm64_atomics_present)                                \
    {                                                           \
        return Lse_##METHOD_INVOC;                              \
    }                                                           \
    else                                                        \
    {                                                           \
        RETURN_TYPE result = INTRINSIC_NAME;                    \
        PAL_InterlockedOperationBarrier();                      \
        return result;                                          \
    }                                                           \
}                                                               \

#endif  // LSE_INSTRUCTIONS_ENABLED_BY_DEFAULT
#else   // !HOST_ARM64

#define Define_InterlockMethod(RETURN_TYPE, METHOD_DECL, METHOD_INVOC, INTRINSIC_NAME) \
EXTERN_C PALIMPORT inline RETURN_TYPE PALAPI METHOD_DECL        \
{                                                               \
    RETURN_TYPE result = INTRINSIC_NAME;                        \
    PAL_InterlockedOperationBarrier();                          \
    return result;                                              \
}                                                               \

#endif  // HOST_ARM64

/*++
Function:
InterlockedAdd

The InterlockedAdd function adds the value of the specified variable
with another specified value. The function prevents more than one thread
from using the same variable simultaneously.

Parameters

lpAddend
[in/out] Pointer to the variable to add.

lpAddend
[in] The value to add.

Return Values

The return value is the resulting added value.
--*/
Define_InterlockMethod(
    LONG,
    InterlockedAdd( IN OUT LONG volatile *lpAddend, IN LONG value),
    InterlockedAdd(lpAddend, value),
    __sync_add_and_fetch(lpAddend, value)
)

Define_InterlockMethod(
    LONGLONG,
    InterlockedAdd64(IN OUT LONGLONG volatile *lpAddend, IN LONGLONG value),
    InterlockedAdd64(lpAddend, value),
    __sync_add_and_fetch(lpAddend, value)
)

/*++
Function:
InterlockedIncrement

The InterlockedIncrement function increments (increases by one) the
value of the specified variable and checks the resulting value. The
function prevents more than one thread from using the same variable
simultaneously.

Parameters

lpAddend
[in/out] Pointer to the variable to increment.

Return Values

The return value is the resulting incremented value.

--*/
Define_InterlockMethod(
    LONG,
    InterlockedIncrement(IN OUT LONG volatile *lpAddend),
    InterlockedIncrement(lpAddend),
    __sync_add_and_fetch(lpAddend, (LONG)1)
)

Define_InterlockMethod(
    LONGLONG,
    InterlockedIncrement64(IN OUT LONGLONG volatile *lpAddend),
    InterlockedIncrement64(lpAddend),
    __sync_add_and_fetch(lpAddend, (LONGLONG)1)
)

/*++
Function:
InterlockedDecrement

The InterlockedDecrement function decrements (decreases by one) the
value of the specified variable and checks the resulting value. The
function prevents more than one thread from using the same variable
simultaneously.

Parameters

lpAddend
[in/out] Pointer to the variable to decrement.

Return Values

The return value is the resulting decremented value.

--*/
Define_InterlockMethod(
    LONG,
    InterlockedDecrement(IN OUT LONG volatile *lpAddend),
    InterlockedDecrement(lpAddend),
    __sync_sub_and_fetch(lpAddend, (LONG)1)
)

#define InterlockedDecrementRelease InterlockedDecrement

Define_InterlockMethod(
    LONGLONG,
    InterlockedDecrement64(IN OUT LONGLONG volatile *lpAddend),
    InterlockedDecrement64(lpAddend),
    __sync_sub_and_fetch(lpAddend, (LONGLONG)1)
)

/*++
Function:
InterlockedExchange

The InterlockedExchange function atomically exchanges a pair of
values. The function prevents more than one thread from using the same
variable simultaneously.

Parameters

Target
[in/out] Pointer to the value to exchange. The function sets
this variable to Value, and returns its prior value.
Value
[in] Specifies a new value for the variable pointed to by Target.

Return Values

The function returns the initial value pointed to by Target.

--*/
Define_InterlockMethod(
    LONG,
    InterlockedExchange(IN OUT LONG volatile *Target, LONG Value),
    InterlockedExchange(Target, Value),
    __atomic_exchange_n(Target, Value, __ATOMIC_ACQ_REL)
)

#if defined(HOST_X86)

// 64-bit __atomic_exchange_n is not expanded as a compiler intrinsic on Linux x86.
// Use inline implementation instead.

inline LONGLONG InterlockedExchange64(LONGLONG volatile * Target, LONGLONG Value)
{
    LONGLONG Old;

    do {
        Old = *Target;
    } while (__sync_val_compare_and_swap(Target, Old, Value) != Old);

    return Old;
}

#else

Define_InterlockMethod(
    LONGLONG,
    InterlockedExchange64(IN OUT LONGLONG volatile *Target, IN LONGLONG Value),
    InterlockedExchange64(Target, Value),
    __atomic_exchange_n(Target, Value, __ATOMIC_ACQ_REL)
)

#endif


/*++
Function:
InterlockedCompareExchange

The InterlockedCompareExchange function performs an atomic comparison
of the specified values and exchanges the values, based on the outcome
of the comparison. The function prevents more than one thread from
using the same variable simultaneously.

If you are exchanging pointer values, this function has been
superseded by the InterlockedCompareExchangePointer function.

Parameters

Destination     [in/out] Specifies the address of the destination value. The sign is ignored.
Exchange        [in]     Specifies the exchange value. The sign is ignored.
Comperand       [in]     Specifies the value to compare to Destination. The sign is ignored.

Return Values

The return value is the initial value of the destination.

--*/
Define_InterlockMethod(
    LONG,
    InterlockedCompareExchange(IN OUT LONG volatile *Destination, IN LONG Exchange, IN LONG Comperand),
    InterlockedCompareExchange(Destination, Exchange, Comperand),
    __sync_val_compare_and_swap(
        Destination, /* The pointer to a variable whose value is to be compared with. */
        Comperand, /* The value to be compared */
        Exchange /* The value to be stored */)
)

#define InterlockedCompareExchangeAcquire InterlockedCompareExchange
#define InterlockedCompareExchangeRelease InterlockedCompareExchange

Define_InterlockMethod(
    LONGLONG,
    InterlockedCompareExchange64(IN OUT LONGLONG volatile *Destination, IN LONGLONG Exchange, IN LONGLONG Comperand),
    InterlockedCompareExchange64(Destination, Exchange, Comperand),
    __sync_val_compare_and_swap(
        Destination, /* The pointer to a variable whose value is to be compared with. */
        Comperand, /* The value to be compared */
        Exchange /* The value to be stored */)
)

/*++
Function:
InterlockedExchangeAdd

The InterlockedExchangeAdd function atomically adds the value of 'Value'
to the variable that 'Addend' points to.

Parameters

lpAddend
[in/out] Pointer to the variable to added.

Return Values

The return value is the original value that 'Addend' pointed to.

--*/
Define_InterlockMethod(
    LONG,
    InterlockedExchangeAdd(IN OUT LONG volatile *Addend, IN LONG Value),
    InterlockedExchangeAdd(Addend, Value),
    __sync_fetch_and_add(Addend, Value)
)

Define_InterlockMethod(
    LONGLONG,
    InterlockedExchangeAdd64(IN OUT LONGLONG volatile *Addend, IN LONGLONG Value),
    InterlockedExchangeAdd64(Addend, Value),
    __sync_fetch_and_add(Addend, Value)
)

Define_InterlockMethod(
    LONG,
    InterlockedAnd(IN OUT LONG volatile *Destination, IN LONG Value),
    InterlockedAnd(Destination, Value),
    __sync_fetch_and_and(Destination, Value)
)

Define_InterlockMethod(
    LONG,
    InterlockedOr(IN OUT LONG volatile *Destination, IN LONG Value),
    InterlockedOr(Destination, Value),
    __sync_fetch_and_or(Destination, Value)
)

#if defined(HOST_64BIT)
#define InterlockedExchangePointer(Target, Value) \
    ((PVOID)InterlockedExchange64((PLONG64)(Target), (LONGLONG)(Value)))

#define InterlockedCompareExchangePointer(Destination, ExChange, Comperand) \
    ((PVOID)InterlockedCompareExchange64((PLONG64)(Destination), (LONGLONG)(ExChange), (LONGLONG)(Comperand)))
#else
#define InterlockedExchangePointer(Target, Value) \
    ((PVOID)(UINT_PTR)InterlockedExchange((PLONG)(UINT_PTR)(Target), (LONG)(UINT_PTR)(Value)))

#define InterlockedCompareExchangePointer(Destination, ExChange, Comperand) \
    ((PVOID)(UINT_PTR)InterlockedCompareExchange((PLONG)(UINT_PTR)(Destination), (LONG)(UINT_PTR)(ExChange), (LONG)(UINT_PTR)(Comperand)))
#endif

#if defined(HOST_64BIT) && defined(FEATURE_CACHED_INTERFACE_DISPATCH)
FORCEINLINE uint8_t _InterlockedCompareExchange128(int64_t volatile *pDst, int64_t iValueHigh, int64_t iValueLow, int64_t *pComparandAndResult)
{
    __int128_t iComparand = ((__int128_t)pComparandAndResult[1] << 64) + (uint64_t)pComparandAndResult[0];
    // TODO-LOONGARCH64: the 128-bit CAS is supported starting from the 3A6000 CPU (ISA1.1).
    // When running on older hardware that doesn't support native CAS-128, the system falls back
    // to a mutex-based approach via libatomic, which is not suitable for runtime requirements.
    //
    // TODO-RISCV64: double-check if libatomic's emulated CAS-128 works as expected once AOT applications are
    // functional on linux-riscv64: https://github.com/dotnet/runtime/issues/106223.
    // CAS-128 is natively supported starting with the Zacas extension in Linux 6.8; however, hardware support
    // for RVA23 profile is not available at the time of writing.
    //
    // See https://github.com/dotnet/runtime/issues/109276.
    __int128_t iResult = __sync_val_compare_and_swap((__int128_t volatile*)pDst, iComparand, ((__int128_t)iValueHigh << 64) + (uint64_t)iValueLow);
    PAL_InterlockedOperationBarrier();
    pComparandAndResult[0] = (int64_t)iResult; pComparandAndResult[1] = (int64_t)(iResult >> 64);
    return iComparand == iResult;
}
#endif

/*++
Function:
MemoryBarrier

The MemoryBarrier function creates a full memory barrier.

--*/
EXTERN_C
PALIMPORT
inline
VOID
PALAPI
MemoryBarrier()
{
    __sync_synchronize();
}

EXTERN_C
PALIMPORT
inline
VOID
PALAPI
YieldProcessor()
{
#if defined(HOST_X86) || defined(HOST_AMD64)
    __asm__ __volatile__(
        "rep\n"
        "nop");
#elif defined(HOST_ARM) || defined(HOST_ARM64)
    __asm__ __volatile__( "yield");
#elif defined(HOST_LOONGARCH64)
    __asm__ volatile( "dbar 0;  \n");
#elif defined(HOST_RISCV64)
    // TODO-RISCV64-CQ: When Zihintpause is supported, replace with `pause` instruction.
    __asm__ __volatile__(".word 0x0100000f");
#else
    return;
#endif
}

#define FORMAT_MESSAGE_ALLOCATE_BUFFER 0x00000100
#define FORMAT_MESSAGE_IGNORE_INSERTS  0x00000200
#define FORMAT_MESSAGE_FROM_STRING     0x00000400
#define FORMAT_MESSAGE_FROM_SYSTEM     0x00001000
#define FORMAT_MESSAGE_ARGUMENT_ARRAY  0x00002000
#define FORMAT_MESSAGE_MAX_WIDTH_MASK  0x000000FF

PALIMPORT
DWORD
PALAPI
FormatMessageW(
           IN DWORD dwFlags,
           IN LPCVOID lpSource,
           IN DWORD dwMessageId,
           IN DWORD dwLanguageId,
           OUT LPWSTR lpBffer,
           IN DWORD nSize,
           IN va_list *Arguments);

#ifdef UNICODE
#define FormatMessage FormatMessageW
#endif


PALIMPORT
DWORD
PALAPI
GetLastError();

PALIMPORT
VOID
PALAPI
SetLastError(
         IN DWORD dwErrCode);

PALIMPORT
LPWSTR
PALAPI
GetCommandLineW();

#ifdef UNICODE
#define GetCommandLine GetCommandLineW
#endif

PALIMPORT
VOID
PALAPI
RtlRestoreContext(
  IN PCONTEXT ContextRecord,
  IN PEXCEPTION_RECORD ExceptionRecord
);

PALIMPORT
VOID
PALAPI
RtlCaptureContext(
  OUT PCONTEXT ContextRecord
);

PALIMPORT
VOID
PALAPI
FlushProcessWriteBuffers();

typedef void (*PAL_ActivationFunction)(CONTEXT *context);
typedef BOOL (*PAL_SafeActivationCheckFunction)(SIZE_T ip);

PALIMPORT
VOID
PALAPI
PAL_SetActivationFunction(
    IN PAL_ActivationFunction pActivationFunction,
    IN PAL_SafeActivationCheckFunction pSafeActivationCheckFunction);

PALIMPORT
BOOL
PALAPI
PAL_InjectActivation(
    IN HANDLE hThread
);

typedef struct _SYSTEM_INFO {
    WORD wProcessorArchitecture_PAL_Undefined;
    WORD wReserved_PAL_Undefined; // NOTE: diff from winbase.h - no obsolete dwOemId union
    DWORD dwPageSize;
    LPVOID lpMinimumApplicationAddress;
    LPVOID lpMaximumApplicationAddress;
    DWORD_PTR dwActiveProcessorMask_PAL_Undefined;
    DWORD dwNumberOfProcessors;
    DWORD dwProcessorType_PAL_Undefined;
    DWORD dwAllocationGranularity;
    WORD wProcessorLevel_PAL_Undefined;
    WORD wProcessorRevision_PAL_Undefined;
} SYSTEM_INFO, *LPSYSTEM_INFO;

PALIMPORT
VOID
PALAPI
GetSystemInfo(
          OUT LPSYSTEM_INFO lpSystemInfo);

PALIMPORT
BOOL
PALAPI
PAL_SetCurrentThreadAffinity(WORD procNo);

PALIMPORT
BOOL
PALAPI
PAL_GetCurrentThreadAffinitySet(SIZE_T size, UINT_PTR* data);

//
// The types of events that can be logged.
//
#define EVENTLOG_SUCCESS                0x0000
#define EVENTLOG_ERROR_TYPE             0x0001
#define EVENTLOG_WARNING_TYPE           0x0002
#define EVENTLOG_INFORMATION_TYPE       0x0004
#define EVENTLOG_AUDIT_SUCCESS          0x0008
#define EVENTLOG_AUDIT_FAILURE          0x0010

#if defined FEATURE_PAL_ANSI
#include "palprivate.h"
#endif //FEATURE_PAL_ANSI
/******************* C Runtime Entrypoints *******************************/

#ifndef _CONST_RETURN
#ifdef  __cplusplus
#define _CONST_RETURN  const
#define _CRT_CONST_CORRECT_OVERLOADS
#else
#define _CONST_RETURN
#endif
#endif

/* For backwards compatibility */
#define _WConst_return _CONST_RETURN

/* _TRUNCATE */
#if !defined(_TRUNCATE)
#define _TRUNCATE ((size_t)-1)
#endif

// errno_t is only defined when the Secure CRT Extensions library is available (which no standard library that we build with implements anyway)
typedef int errno_t;

PALIMPORT DLLEXPORT errno_t __cdecl memcpy_s(void *, size_t, const void *, size_t);
PALIMPORT errno_t __cdecl memmove_s(void *, size_t, const void *, size_t);
PALIMPORT DLLEXPORT int __cdecl _wcsicmp(const WCHAR *, const WCHAR*);
PALIMPORT int __cdecl _wcsnicmp(const WCHAR *, const WCHAR *, size_t);
PALIMPORT DLLEXPORT int __cdecl _vsnprintf_s(char *, size_t, size_t, const char *, va_list);
PALIMPORT DLLEXPORT int __cdecl _snprintf_s(char *, size_t, size_t, const char *, ...);
PALIMPORT DLLEXPORT int __cdecl vsprintf_s(char *, size_t, const char *, va_list);
PALIMPORT DLLEXPORT int __cdecl sprintf_s(char *, size_t, const char *, ... );
PALIMPORT DLLEXPORT int __cdecl sscanf_s(const char *, const char *, ...);

PALIMPORT DLLEXPORT size_t __cdecl PAL_wcslen(const WCHAR *);
PALIMPORT DLLEXPORT int __cdecl PAL_wcscmp(const WCHAR*, const WCHAR*);
PALIMPORT DLLEXPORT int __cdecl PAL_wcsncmp(const WCHAR *, const WCHAR *, size_t);
PALIMPORT DLLEXPORT WCHAR * __cdecl PAL_wcscat(WCHAR *, const WCHAR *);
PALIMPORT WCHAR * __cdecl PAL_wcscpy(WCHAR *, const WCHAR *);
PALIMPORT WCHAR * __cdecl PAL_wcsncpy(WCHAR *, const WCHAR *, size_t);
PALIMPORT DLLEXPORT const WCHAR * __cdecl PAL_wcschr(const WCHAR *, WCHAR);
PALIMPORT DLLEXPORT const WCHAR * __cdecl PAL_wcsrchr(const WCHAR *, WCHAR);
PALIMPORT WCHAR _WConst_return * __cdecl PAL_wcspbrk(const WCHAR *, const WCHAR *);
PALIMPORT DLLEXPORT WCHAR _WConst_return * __cdecl PAL_wcsstr(const WCHAR *, const WCHAR *);
PALIMPORT DLLEXPORT ULONG __cdecl PAL_wcstoul(const WCHAR *, WCHAR **, int);
PALIMPORT DLLEXPORT ULONGLONG __cdecl PAL__wcstoui64(const WCHAR *, WCHAR **, int);
PALIMPORT DLLEXPORT double __cdecl PAL_wcstod(const WCHAR *, WCHAR **);

PALIMPORT errno_t __cdecl _wcslwr_s(WCHAR *, size_t sz);
PALIMPORT int __cdecl _wtoi(const WCHAR *);
PALIMPORT FILE * __cdecl _wfopen(const WCHAR *, const WCHAR *);

inline int _stricmp(const char* a, const char* b)
{
    return strcasecmp(a, b);
}

inline int _strnicmp(const char* a, const char* b, size_t c)
{
    return strncasecmp(a, b, c);
}

inline char* _strdup(const char* a)
{
    return strdup(a);
}

// Define the MSVC implementation of the alloca concept.
// As this allocates on the current stack frame, use a macro instead of an inline function.
#define _alloca(x) alloca(x)

#ifdef __cplusplus
extern "C++" {
inline WCHAR *PAL_wcschr(WCHAR* S, WCHAR C)
        {return ((WCHAR *)PAL_wcschr((const WCHAR *)S, C)); }
inline WCHAR *PAL_wcsrchr(WCHAR* S, WCHAR C)
        {return ((WCHAR *)PAL_wcsrchr((const WCHAR *)S, C)); }
inline WCHAR *PAL_wcspbrk(WCHAR* S, const WCHAR* P)
        {return ((WCHAR *)PAL_wcspbrk((const WCHAR *)S, P)); }
inline WCHAR *PAL_wcsstr(WCHAR* S, const WCHAR* P)
        {return ((WCHAR *)PAL_wcsstr((const WCHAR *)S, P)); }
}
#endif

#if !__has_builtin(_rotl) && !defined(_rotl)
/*++
Function:
_rotl

See MSDN doc.
--*/
EXTERN_C
PALIMPORT
inline
unsigned int __cdecl _rotl(unsigned int value, int shift)
{
    unsigned int retval = 0;

    shift &= 0x1f;
    retval = (value << shift) | (value >> (sizeof(int) * CHAR_BIT - shift));
    return retval;
}
#endif // !__has_builtin(_rotl)

#if !__has_builtin(_rotr) && !defined(_rotr)

/*++
Function:
_rotr

See MSDN doc.
--*/
EXTERN_C
PALIMPORT
inline
unsigned int __cdecl _rotr(unsigned int value, int shift)
{
    unsigned int retval;

    shift &= 0x1f;
    retval = (value >> shift) | (value << (sizeof(int) * CHAR_BIT - shift));
    return retval;
}

#endif // !__has_builtin(_rotr)

PALIMPORT DLLEXPORT char * __cdecl PAL_getenv(const char *);
PALIMPORT DLLEXPORT int __cdecl _putenv(const char *);

#ifndef ERANGE
#define ERANGE          34
#endif

/******************* PAL functions for exceptions *******/

#ifdef __cplusplus

PALIMPORT
VOID
PALAPI
PAL_FreeExceptionRecords(
  IN EXCEPTION_RECORD *exceptionRecord,
  IN CONTEXT *contextRecord);

#define EXCEPTION_CONTINUE_SEARCH   0
#define EXCEPTION_EXECUTE_HANDLER   1
#define EXCEPTION_CONTINUE_EXECUTION -1

struct PAL_SEHException
{
private:
    static const SIZE_T NoTargetFrameSp = (SIZE_T)SIZE_MAX;

    void Move(PAL_SEHException& ex)
    {
        ExceptionPointers.ExceptionRecord = ex.ExceptionPointers.ExceptionRecord;
        ExceptionPointers.ContextRecord = ex.ExceptionPointers.ContextRecord;
        TargetFrameSp = ex.TargetFrameSp;
        TargetIp = ex.TargetIp;
        RecordsOnStack = ex.RecordsOnStack;
        IsExternal = ex.IsExternal;
        ManagedToNativeExceptionCallback = ex.ManagedToNativeExceptionCallback;
        ManagedToNativeExceptionCallbackContext = ex.ManagedToNativeExceptionCallbackContext;

        ex.Clear();
    }

    void FreeRecords()
    {
        if (ExceptionPointers.ExceptionRecord != NULL && !RecordsOnStack )
        {
            PAL_FreeExceptionRecords(ExceptionPointers.ExceptionRecord, ExceptionPointers.ContextRecord);
            ExceptionPointers.ExceptionRecord = NULL;
            ExceptionPointers.ContextRecord = NULL;
        }
    }

public:
    EXCEPTION_POINTERS ExceptionPointers;
    // Target frame stack pointer set before the 2nd pass.
    SIZE_T TargetFrameSp;
    SIZE_T TargetIp;
    SIZE_T ReturnValue;
    bool RecordsOnStack;
    // The exception is a hardware exception coming from a native code out of
    // the well known runtime helpers
    bool IsExternal;

    void(*ManagedToNativeExceptionCallback)(void* context);
    void* ManagedToNativeExceptionCallbackContext;

    PAL_SEHException(EXCEPTION_RECORD *pExceptionRecord, CONTEXT *pContextRecord, bool onStack = false)
    {
        ExceptionPointers.ExceptionRecord = pExceptionRecord;
        ExceptionPointers.ContextRecord = pContextRecord;
        TargetFrameSp = NoTargetFrameSp;
        TargetIp = 0;
        RecordsOnStack = onStack;
        IsExternal = false;
        ManagedToNativeExceptionCallback = NULL;
        ManagedToNativeExceptionCallbackContext = NULL;
    }

    PAL_SEHException()
    {
        Clear();
    }

    // The copy constructor and copy assignment operators are deleted so that the PAL_SEHException
    // can never be copied, only moved. This enables simple lifetime management of the exception and
    // context records, since there is always just one PAL_SEHException instance referring to the same records.
    PAL_SEHException(const PAL_SEHException& ex) = delete;
    PAL_SEHException& operator=(const PAL_SEHException& ex) = delete;

    PAL_SEHException(PAL_SEHException&& ex)
    {
        Move(ex);
    }

    PAL_SEHException& operator=(PAL_SEHException&& ex)
    {
        FreeRecords();
        Move(ex);
        return *this;
    }

    ~PAL_SEHException()
    {
        FreeRecords();
    }

    void Clear()
    {
        ExceptionPointers.ExceptionRecord = NULL;
        ExceptionPointers.ContextRecord = NULL;
        TargetFrameSp = NoTargetFrameSp;
        TargetIp = 0;
        RecordsOnStack = false;
        IsExternal = false;
        ManagedToNativeExceptionCallback = NULL;
        ManagedToNativeExceptionCallbackContext = NULL;
    }

    CONTEXT* GetContextRecord()
    {
        return ExceptionPointers.ContextRecord;
    }

    EXCEPTION_RECORD* GetExceptionRecord()
    {
        return ExceptionPointers.ExceptionRecord;
    }

    bool IsFirstPass()
    {
        return (TargetFrameSp == NoTargetFrameSp);
    }

    void SecondPassDone()
    {
        TargetFrameSp = NoTargetFrameSp;
    }

    bool HasPropagateExceptionCallback()
    {
        return ManagedToNativeExceptionCallback != NULL;
    }

    void SetPropagateExceptionCallback(
        void(*callback)(void*),
        void* context)
    {
        ManagedToNativeExceptionCallback = callback;
        ManagedToNativeExceptionCallbackContext = context;
    }
};

typedef BOOL (*PHARDWARE_EXCEPTION_HANDLER)(PAL_SEHException* ex);
typedef BOOL (*PHARDWARE_EXCEPTION_SAFETY_CHECK_FUNCTION)(PCONTEXT contextRecord, PEXCEPTION_RECORD exceptionRecord);
typedef DWORD (*PGET_GCMARKER_EXCEPTION_CODE)(LPVOID ip);

PALIMPORT
VOID
PALAPI
PAL_SetHardwareExceptionHandler(
    IN PHARDWARE_EXCEPTION_HANDLER exceptionHandler,
    IN PHARDWARE_EXCEPTION_SAFETY_CHECK_FUNCTION exceptionCheckFunction);

PALIMPORT
VOID
PALAPI
PAL_SetGetGcMarkerExceptionCode(
    IN PGET_GCMARKER_EXCEPTION_CODE getGcMarkerExceptionCode);

PALIMPORT
VOID
PALAPI
PAL_ThrowExceptionFromContext(
    IN CONTEXT* context,
    IN PAL_SEHException* ex);

PALIMPORT
VOID
PALAPI
PAL_CatchHardwareExceptionHolderEnter();

PALIMPORT
VOID
PALAPI
PAL_CatchHardwareExceptionHolderExit();

//
// This holder is used to indicate that a hardware
// exception should be raised as a C++ exception
// to better emulate SEH on the xplat platforms.
//
class CatchHardwareExceptionHolder
{
public:
    CatchHardwareExceptionHolder()
    {
        PAL_CatchHardwareExceptionHolderEnter();
    }

    ~CatchHardwareExceptionHolder()
    {
        PAL_CatchHardwareExceptionHolderExit();
    }

    static bool IsEnabled();
};

//
// NOTE: This is only defined in one PAL test.
//
#ifdef FEATURE_ENABLE_HARDWARE_EXCEPTIONS
#define HardwareExceptionHolder CatchHardwareExceptionHolder __catchHardwareException;
#else
#define HardwareExceptionHolder
#endif // FEATURE_ENABLE_HARDWARE_EXCEPTIONS

class NativeExceptionHolderBase;

PALIMPORT
PALAPI
NativeExceptionHolderBase **
PAL_GetNativeExceptionHolderHead();

extern "C++" {

//
// This is the base class of native exception holder used to provide
// the filter function to the exception dispatcher. This allows the
// filter to be called during the first pass to better emulate SEH
// the xplat platforms that only have C++ exception support.
//
class NativeExceptionHolderBase
{
    // Save the address of the holder head so the destructor
    // doesn't have access the slow (on Linux) TLS value again.
    NativeExceptionHolderBase **m_head;

    // The next holder on the stack
    NativeExceptionHolderBase *m_next;

protected:
    NativeExceptionHolderBase()
    {
        m_head = nullptr;
        m_next = nullptr;
    }

    ~NativeExceptionHolderBase()
    {
        // Only destroy if Push was called
        if (m_head != nullptr)
        {
            *m_head = m_next;
            m_head = nullptr;
            m_next = nullptr;
        }
    }

public:
    // Calls the holder's filter handler.
    virtual EXCEPTION_DISPOSITION InvokeFilter(PAL_SEHException& ex) = 0;

    // Adds the holder to the "stack" of holders. This is done explicitly instead
    // of in the constructor was to avoid the mess of move constructors combined
    // with return value optimization (in CreateHolder).
    void Push()
    {
        NativeExceptionHolderBase **head = PAL_GetNativeExceptionHolderHead();
        m_head = head;
        m_next = *head;
        *head = this;
    }

    // Given the currentHolder and locals stack range find the next holder starting with this one
    // To find the first holder, pass nullptr as the currentHolder.
    static NativeExceptionHolderBase *FindNextHolder(NativeExceptionHolderBase *currentHolder, PVOID frameLowAddress, PVOID frameHighAddress);
};

//
// This is the second part of the native exception filter holder. It is
// templated because the lambda used to wrap the exception filter is a
// unknown type.
//
template<class FilterType>
class NativeExceptionHolder : public NativeExceptionHolderBase
{
    FilterType* m_exceptionFilter;

public:
    NativeExceptionHolder(FilterType* exceptionFilter)
        : NativeExceptionHolderBase()
    {
        m_exceptionFilter = exceptionFilter;
    }

    virtual EXCEPTION_DISPOSITION InvokeFilter(PAL_SEHException& ex)
    {
        return (*m_exceptionFilter)(ex);
    }
};

//
// This is a native exception holder that is used when the catch catches
// all exceptions.
//
class NativeExceptionHolderCatchAll : public NativeExceptionHolderBase
{

public:
    NativeExceptionHolderCatchAll()
        : NativeExceptionHolderBase()
    {
    }

    virtual EXCEPTION_DISPOSITION InvokeFilter(PAL_SEHException& ex)
    {
        return EXCEPTION_EXECUTE_HANDLER;
    }
};

// This is a native exception holder that doesn't catch any exceptions.
class NativeExceptionHolderNoCatch : public NativeExceptionHolderBase
{

public:
    NativeExceptionHolderNoCatch()
        : NativeExceptionHolderBase()
    {
    }

    virtual EXCEPTION_DISPOSITION InvokeFilter(PAL_SEHException& ex)
    {
        return EXCEPTION_CONTINUE_SEARCH;
    }
};

//
// This factory class for the native exception holder is necessary because
// templated functions don't need the explicit type parameter and can infer
// the template type from the parameter.
//
class NativeExceptionHolderFactory
{
public:
    template<class FilterType>
    static NativeExceptionHolder<FilterType> CreateHolder(FilterType* exceptionFilter)
    {
        return NativeExceptionHolder<FilterType>(exceptionFilter);
    }
};

// Start of a try block for exceptions raised by RaiseException
#define PAL_TRY(__ParamType, __paramDef, __paramRef)                            \
{                                                                               \
    __ParamType __param = __paramRef;                                           \
    auto tryBlock = [](__ParamType __paramDef)                                  \
    {

// Start of an exception handler. If an exception raised by the RaiseException
// occurs in the try block and the disposition is EXCEPTION_EXECUTE_HANDLER,
// the handler code is executed. If the disposition is EXCEPTION_CONTINUE_SEARCH,
// the exception is rethrown. The EXCEPTION_CONTINUE_EXECUTION disposition is
// not supported.
#define PAL_EXCEPT(dispositionExpression)                                       \
    };                                                                          \
    const bool isFinally = false;                                               \
    auto finallyBlock = []() {};                                                \
    EXCEPTION_DISPOSITION disposition = EXCEPTION_CONTINUE_EXECUTION;           \
    auto exceptionFilter = [&disposition, &__param](PAL_SEHException& ex)       \
    {                                                                           \
        (void)__param;                                                          \
        disposition = dispositionExpression;                                    \
        _ASSERTE(disposition != EXCEPTION_CONTINUE_EXECUTION);                  \
        return disposition;                                                     \
    };                                                                          \
    try                                                                         \
    {                                                                           \
        HardwareExceptionHolder                                                 \
        auto __exceptionHolder = NativeExceptionHolderFactory::CreateHolder(&exceptionFilter); \
        __exceptionHolder.Push();                                               \
        tryBlock(__param);                                                      \
    }                                                                           \
    catch (PAL_SEHException& ex)                                                \
    {                                                                           \
        if (disposition == EXCEPTION_CONTINUE_EXECUTION)                        \
        {                                                                       \
            exceptionFilter(ex);                                                \
        }                                                                       \
        if (disposition == EXCEPTION_CONTINUE_SEARCH)                           \
        {                                                                       \
            throw;                                                              \
        }                                                                       \
        ex.SecondPassDone();

// Start of an exception handler. It works the same way as the PAL_EXCEPT except
// that the disposition is obtained by calling the specified filter.
#define PAL_EXCEPT_FILTER(filter) PAL_EXCEPT(filter(&ex.ExceptionPointers, __param))

// Start of a finally block. The finally block is executed both when the try block
// finishes or when an exception is raised using the RaiseException in it.
#define PAL_FINALLY                     \
    };                                  \
    const bool isFinally = true;        \
    auto finallyBlock = [&]()           \
    {

// End of an except or a finally block.
#define PAL_ENDTRY                      \
    };                                  \
    if (isFinally)                      \
    {                                   \
        try                             \
        {                               \
            HardwareExceptionHolder     \
            tryBlock(__param);          \
        }                               \
        catch (...)                     \
        {                               \
            finallyBlock();             \
            throw;                      \
        }                               \
        finallyBlock();                 \
    }                                   \
}

} // extern "C++"

#define PAL_CPP_THROW(type, obj) { throw obj; }
#define PAL_CPP_RETHROW { throw; }
#define PAL_CPP_TRY                     try { HardwareExceptionHolder
#define PAL_CPP_CATCH_EXCEPTION(ident)  } catch (Exception *ident) {
#define PAL_CPP_CATCH_EXCEPTION_NOARG   } catch (Exception *) {
#define PAL_CPP_CATCH_DERIVED(type, ident) } catch (type *ident) {
#define PAL_CPP_CATCH_NON_DERIVED(type, ident) } catch (type ident) {
#define PAL_CPP_CATCH_NON_DERIVED_NOARG(type) } catch (type) {
#define PAL_CPP_CATCH_ALL               } catch (...) {                                           \
                                            try { throw; }                                        \
                                            catch (PAL_SEHException& ex) { ex.SecondPassDone(); } \
                                            catch (...) {}

#define PAL_CPP_ENDTRY                  }

#define PAL_TRY_FOR_DLLMAIN(ParamType, paramDef, paramRef, _reason) PAL_TRY(ParamType, paramDef, paramRef)

#endif // __cplusplus

// Platform-specific library naming
//
#ifdef __APPLE__
#define MAKEDLLNAME_W(name) u"lib" name u".dylib"
#define MAKEDLLNAME_A(name)  "lib" name  ".dylib"
#else
#define MAKEDLLNAME_W(name) u"lib" name u".so"
#define MAKEDLLNAME_A(name)  "lib" name  ".so"
#endif

#ifdef UNICODE
#define MAKEDLLNAME(x) MAKEDLLNAME_W(x)
#else
#define MAKEDLLNAME(x) MAKEDLLNAME_A(x)
#endif

#define PAL_SHLIB_PREFIX    "lib"
#define PAL_SHLIB_PREFIX_W  u"lib"

#if __APPLE__
#define PAL_SHLIB_SUFFIX    ".dylib"
#define PAL_SHLIB_SUFFIX_W  u".dylib"
#else
#define PAL_SHLIB_SUFFIX    ".so"
#define PAL_SHLIB_SUFFIX_W  u".so"
#endif

#define DBG_EXCEPTION_HANDLED            ((DWORD   )0x00010001L)
#define DBG_CONTINUE                     ((DWORD   )0x00010002L)
#define DBG_EXCEPTION_NOT_HANDLED        ((DWORD   )0x80010001L)

#define DBG_TERMINATE_THREAD             ((DWORD   )0x40010003L)
#define DBG_TERMINATE_PROCESS            ((DWORD   )0x40010004L)
#define DBG_CONTROL_C                    ((DWORD   )0x40010005L)
#define DBG_RIPEXCEPTION                 ((DWORD   )0x40010007L)
#define DBG_CONTROL_BREAK                ((DWORD   )0x40010008L)
#define DBG_COMMAND_EXCEPTION            ((DWORD   )0x40010009L)

#define STATUS_USER_APC                  ((DWORD   )0x000000C0L)
#define STATUS_GUARD_PAGE_VIOLATION      ((DWORD   )0x80000001L)
#define STATUS_DATATYPE_MISALIGNMENT     ((DWORD   )0x80000002L)
#define STATUS_BREAKPOINT                ((DWORD   )0x80000003L)
#define STATUS_SINGLE_STEP               ((DWORD   )0x80000004L)
#define STATUS_LONGJUMP                  ((DWORD   )0x80000026L)
#define STATUS_UNWIND_CONSOLIDATE        ((DWORD   )0x80000029L)
#define STATUS_ACCESS_VIOLATION          ((DWORD   )0xC0000005L)
#define STATUS_IN_PAGE_ERROR             ((DWORD   )0xC0000006L)
#define STATUS_INVALID_HANDLE            ((DWORD   )0xC0000008L)
#define STATUS_NO_MEMORY                 ((DWORD   )0xC0000017L)
#define STATUS_ILLEGAL_INSTRUCTION       ((DWORD   )0xC000001DL)
#define STATUS_NONCONTINUABLE_EXCEPTION  ((DWORD   )0xC0000025L)
#define STATUS_INVALID_DISPOSITION       ((DWORD   )0xC0000026L)
#define STATUS_ARRAY_BOUNDS_EXCEEDED     ((DWORD   )0xC000008CL)
#define STATUS_FLOAT_DENORMAL_OPERAND    ((DWORD   )0xC000008DL)
#define STATUS_FLOAT_DIVIDE_BY_ZERO      ((DWORD   )0xC000008EL)
#define STATUS_FLOAT_INEXACT_RESULT      ((DWORD   )0xC000008FL)
#define STATUS_FLOAT_INVALID_OPERATION   ((DWORD   )0xC0000090L)
#define STATUS_FLOAT_OVERFLOW            ((DWORD   )0xC0000091L)
#define STATUS_FLOAT_STACK_CHECK         ((DWORD   )0xC0000092L)
#define STATUS_FLOAT_UNDERFLOW           ((DWORD   )0xC0000093L)
#define STATUS_INTEGER_DIVIDE_BY_ZERO    ((DWORD   )0xC0000094L)
#define STATUS_INTEGER_OVERFLOW          ((DWORD   )0xC0000095L)
#define STATUS_PRIVILEGED_INSTRUCTION    ((DWORD   )0xC0000096L)
#define STATUS_STACK_OVERFLOW            ((DWORD   )0xC00000FDL)
#define STATUS_CONTROL_C_EXIT            ((DWORD   )0xC000013AL)

#define WAIT_IO_COMPLETION                  STATUS_USER_APC

#define EXCEPTION_ACCESS_VIOLATION          STATUS_ACCESS_VIOLATION
#define EXCEPTION_DATATYPE_MISALIGNMENT     STATUS_DATATYPE_MISALIGNMENT
#define EXCEPTION_BREAKPOINT                STATUS_BREAKPOINT
#define EXCEPTION_SINGLE_STEP               STATUS_SINGLE_STEP
#define EXCEPTION_ARRAY_BOUNDS_EXCEEDED     STATUS_ARRAY_BOUNDS_EXCEEDED
#define EXCEPTION_FLT_DENORMAL_OPERAND      STATUS_FLOAT_DENORMAL_OPERAND
#define EXCEPTION_FLT_DIVIDE_BY_ZERO        STATUS_FLOAT_DIVIDE_BY_ZERO
#define EXCEPTION_FLT_INEXACT_RESULT        STATUS_FLOAT_INEXACT_RESULT
#define EXCEPTION_FLT_INVALID_OPERATION     STATUS_FLOAT_INVALID_OPERATION
#define EXCEPTION_FLT_OVERFLOW              STATUS_FLOAT_OVERFLOW
#define EXCEPTION_FLT_STACK_CHECK           STATUS_FLOAT_STACK_CHECK
#define EXCEPTION_FLT_UNDERFLOW             STATUS_FLOAT_UNDERFLOW
#define EXCEPTION_INT_DIVIDE_BY_ZERO        STATUS_INTEGER_DIVIDE_BY_ZERO
#define EXCEPTION_INT_OVERFLOW              STATUS_INTEGER_OVERFLOW
#define EXCEPTION_PRIV_INSTRUCTION          STATUS_PRIVILEGED_INSTRUCTION
#define EXCEPTION_IN_PAGE_ERROR             STATUS_IN_PAGE_ERROR
#define EXCEPTION_ILLEGAL_INSTRUCTION       STATUS_ILLEGAL_INSTRUCTION
#define EXCEPTION_NONCONTINUABLE_EXCEPTION  STATUS_NONCONTINUABLE_EXCEPTION
#define EXCEPTION_STACK_OVERFLOW            STATUS_STACK_OVERFLOW
#define EXCEPTION_INVALID_DISPOSITION       STATUS_INVALID_DISPOSITION
#define EXCEPTION_GUARD_PAGE                STATUS_GUARD_PAGE_VIOLATION
#define EXCEPTION_INVALID_HANDLE            STATUS_INVALID_HANDLE

#define CONTROL_C_EXIT                      STATUS_CONTROL_C_EXIT

/******************* HRESULT types ****************************************/

#define FACILITY_ITF                     4
#define FACILITY_WIN32                   7
#define FACILITY_CONTROL                 10
#define FACILITY_URT                     19

#define NO_ERROR 0L

#define SEVERITY_SUCCESS    0
#define SEVERITY_ERROR      1

#define SUCCEEDED(Status) ((HRESULT)(Status) >= 0)
#define FAILED(Status) ((HRESULT)(Status)<0)
#define HRESULT_CODE(hr)    ((hr) & 0xFFFF)
#define HRESULT_FACILITY(hr)  (((hr) >> 16) & 0x1fff)

// both macros diff from Win32
#define MAKE_HRESULT(sev,fac,code) \
    ((HRESULT) (((ULONG)(sev)<<31) | ((ULONG)(fac)<<16) | ((ULONG)(code))) )
#define MAKE_SCODE(sev,fac,code) \
    ((SCODE) (((ULONG)(sev)<<31) | ((ULONG)(fac)<<16) | ((LONG)(code))) )

#define FACILITY_NT_BIT                 0x10000000
#define HRESULT_FROM_WIN32(x) ((HRESULT)(x) <= 0 ? ((HRESULT)(x)) : ((HRESULT) (((x) & 0x0000FFFF) | (FACILITY_WIN32 << 16) | 0x80000000)))
#define __HRESULT_FROM_WIN32(x) HRESULT_FROM_WIN32(x)

#define HRESULT_FROM_NT(x)      ((HRESULT) ((x) | FACILITY_NT_BIT))

#ifdef  __cplusplus
}
#endif

#ifndef TARGET_WASM
#define _ReturnAddress() __builtin_return_address(0)
#else
#define _ReturnAddress() 0
#endif

#endif // __PAL_H__
