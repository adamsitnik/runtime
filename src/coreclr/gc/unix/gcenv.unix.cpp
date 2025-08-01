// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#define _WITH_GETLINE
#include <cstdint>
#include <cstddef>
#include <cstdio>
#include <cassert>
#define __STDC_FORMAT_MACROS
#include <cinttypes>
#include <memory>
#include <pthread.h>
#include <signal.h>

#include "config.gc.h"
#include "common.h"

#include "gcenv.structs.h"
#include "gcenv.base.h"
#include "gcenv.os.h"
#include "gcenv.ee.h"
#include "gcenv.unix.inl"
#include "volatile.h"
#include "gcconfig.h"
#include "numasupport.h"
#include <minipal/thread.h>
#include <minipal/time.h>

#if HAVE_SWAPCTL
#include <sys/swap.h>
#endif

#ifdef __linux__
#include <linux/membarrier.h>
#include <sys/syscall.h>
#define membarrier(...) syscall(__NR_membarrier, __VA_ARGS__)
#elif HAVE_SYS_MEMBARRIER_H
#include <sys/membarrier.h>
#ifdef TARGET_BROWSER
#define membarrier(cmd, flags, cpu_id) 0 // browser/wasm is currently single threaded
#endif
#endif

#include <sys/resource.h>

#undef min
#undef max

#ifndef __has_cpp_attribute
#define __has_cpp_attribute(x) (0)
#endif

#include <algorithm>

#if HAVE_SYS_TIME_H
 #include <sys/time.h>
#else
 #error "sys/time.h required by GC PAL for the time being"
#endif

#if HAVE_SYS_MMAN_H
 #include <sys/mman.h>
#else
 #error "sys/mman.h required by GC PAL"
#endif

#if HAVE_SYSCTLBYNAME
#include <sys/types.h>
#include <sys/sysctl.h>
#endif

#if HAVE_SYSINFO
#include <sys/sysinfo.h>
#endif

#if HAVE_XSWDEV
#include <vm/vm_param.h>
#endif // HAVE_XSWDEV

#ifdef __APPLE__
#include <mach/vm_types.h>
#include <mach/vm_param.h>
#include <mach/mach_port.h>
#include <mach/mach_host.h>

#include <mach/task.h>
#include <mach/vm_map.h>
extern "C"
{
#  include <mach/thread_state.h>
}

#define CHECK_MACH(_msg, machret) do {                                      \
        if (machret != KERN_SUCCESS)                                        \
        {                                                                   \
            char _szError[1024];                                            \
            snprintf(_szError, ARRAY_SIZE(_szError), "%s: %u: %s", __FUNCTION__, __LINE__, _msg);  \
            mach_error(_szError, machret);                                  \
            abort();                                                        \
        }                                                                   \
    } while (false)

#endif // __APPLE__

#ifdef __HAIKU__
#include <OS.h>
#endif // __HAIKU__

#if HAVE_PTHREAD_NP_H
#include <pthread_np.h>
#endif

#if HAVE_CPUSET_T
typedef cpuset_t cpu_set_t;
#endif

#include <time.h> // nanosleep
#include <sched.h> // sched_yield
#include <errno.h>
#include <unistd.h> // sysconf
#include "globals.h"
#include "cgroup.h"

#ifndef __APPLE__
#if HAVE_SYSCONF && HAVE__SC_AVPHYS_PAGES
#define SYSCONF_PAGES _SC_AVPHYS_PAGES
#elif HAVE_SYSCONF && HAVE__SC_PHYS_PAGES
#define SYSCONF_PAGES _SC_PHYS_PAGES
#else
#error Dont know how to get page-size on this architecture!
#endif
#endif // __APPLE__

#if defined(HOST_ARM) || defined(HOST_ARM64) || defined(HOST_LOONGARCH64) || defined(HOST_RISCV64)
#define SYSCONF_GET_NUMPROCS _SC_NPROCESSORS_CONF
#else
#define SYSCONF_GET_NUMPROCS _SC_NPROCESSORS_ONLN
#endif

// The cached total number of CPUs that can be used in the OS.
static uint32_t g_totalCpuCount = 0;

bool CanFlushUsingMembarrier()
{
#if defined(__linux__) || HAVE_SYS_MEMBARRIER_H

#ifdef TARGET_ANDROID
    // Avoid calling membarrier on older Android versions where membarrier
    // may be barred by seccomp causing the process to be killed.
    int apiLevel = android_get_device_api_level();
    if (apiLevel < __ANDROID_API_Q__)
    {
        return false;
    }
#endif

    // Starting with Linux kernel 4.14, process memory barriers can be generated
    // using MEMBARRIER_CMD_PRIVATE_EXPEDITED.

    int mask = membarrier(MEMBARRIER_CMD_QUERY, 0, 0);

    if (mask >= 0 &&
        mask & MEMBARRIER_CMD_PRIVATE_EXPEDITED &&
        // Register intent to use the private expedited command.
        membarrier(MEMBARRIER_CMD_REGISTER_PRIVATE_EXPEDITED, 0, 0) == 0)
    {
        return true;
    }
#endif

    return false;
}

//
// Tracks if the OS supports FlushProcessWriteBuffers using membarrier
//
static int s_flushUsingMemBarrier = 0;

// Helper memory page used by the FlushProcessWriteBuffers
static uint8_t* g_helperPage = 0;

// Mutex to make the FlushProcessWriteBuffersMutex thread safe
static pthread_mutex_t g_flushProcessWriteBuffersMutex;

size_t GetRestrictedPhysicalMemoryLimit();
bool GetPhysicalMemoryUsed(size_t* val);

static size_t g_RestrictedPhysicalMemoryLimit = 0;

uint32_t g_pageSizeUnixInl = 0;

AffinitySet g_processAffinitySet;

extern "C" int g_highestNumaNode;
extern "C" bool g_numaAvailable;

static int64_t g_totalPhysicalMemSize = 0;

#ifdef TARGET_APPLE
static int *g_kern_memorystatus_level_mib = NULL;
static size_t g_kern_memorystatus_level_mib_length = 0;
#endif

// Initialize the interface implementation
// Return:
//  true if it has succeeded, false if it has failed
bool GCToOSInterface::Initialize()
{
    int pageSize = sysconf( _SC_PAGE_SIZE );

    g_pageSizeUnixInl = uint32_t((pageSize > 0) ? pageSize : 0x1000);

    // Calculate and cache the number of processors on this machine
    int cpuCount = sysconf(SYSCONF_GET_NUMPROCS);
    if (cpuCount == -1)
    {
        return false;
    }

    g_totalCpuCount = cpuCount;

    //
    // support for FlusProcessWriteBuffers
    //
#ifndef TARGET_WASM
    assert(s_flushUsingMemBarrier == 0);

    if (CanFlushUsingMembarrier())
    {
        s_flushUsingMemBarrier = TRUE;
    }
#ifndef TARGET_APPLE
    else
    {
        assert(g_helperPage == 0);

        g_helperPage = static_cast<uint8_t*>(mmap(0, OS_PAGE_SIZE, PROT_READ | PROT_WRITE, MAP_ANONYMOUS | MAP_PRIVATE, -1, 0));

        if (g_helperPage == MAP_FAILED)
        {
            return false;
        }

        // Verify that the s_helperPage is really aligned to the g_SystemInfo.dwPageSize
        assert((((size_t)g_helperPage) & (OS_PAGE_SIZE - 1)) == 0);

        // Locking the page ensures that it stays in memory during the two mprotect
        // calls in the FlushProcessWriteBuffers below. If the page was unmapped between
        // those calls, they would not have the expected effect of generating IPI.
        int status = mlock(g_helperPage, OS_PAGE_SIZE);

        if (status != 0)
        {
            return false;
        }

        status = pthread_mutex_init(&g_flushProcessWriteBuffersMutex, NULL);
        if (status != 0)
        {
            munlock(g_helperPage, OS_PAGE_SIZE);
            return false;
        }
    }
#endif // !TARGET_APPLE
#endif // !TARGET_WASM

    InitializeCGroup();

#if HAVE_SCHED_GETAFFINITY

    cpu_set_t cpuSet;
    int st = sched_getaffinity(getpid(), sizeof(cpu_set_t), &cpuSet);

    if (st == 0)
    {
        for (size_t i = 0; i < CPU_SETSIZE; i++)
        {
            if (CPU_ISSET(i, &cpuSet))
            {
                g_processAffinitySet.Add(i);
            }
        }
    }
    else
    {
        // We should not get any of the errors that the sched_getaffinity can return since none
        // of them applies for the current thread, so this is an unexpected kind of failure.
        assert(false);
    }

#else // HAVE_SCHED_GETAFFINITY

    for (size_t i = 0; i < g_totalCpuCount; i++)
    {
        g_processAffinitySet.Add(i);
    }

#endif // HAVE_SCHED_GETAFFINITY

    NUMASupportInitialize();

#ifdef TARGET_APPLE
    const char* mem_free_name = "kern.memorystatus_level";
    int rc = sysctlnametomib(mem_free_name, NULL, &g_kern_memorystatus_level_mib_length);
    if (rc != 0)
    {
        return false;
    }

    g_kern_memorystatus_level_mib = (int*)malloc(g_kern_memorystatus_level_mib_length * sizeof(int));
    if (g_kern_memorystatus_level_mib == NULL)
    {
        return false;
    }

    rc = sysctlnametomib(mem_free_name, g_kern_memorystatus_level_mib, &g_kern_memorystatus_level_mib_length);
    if (rc != 0)
    {
        free(g_kern_memorystatus_level_mib);
        g_kern_memorystatus_level_mib = NULL;
        g_kern_memorystatus_level_mib_length = 0;
        return false;
    }
#endif

    // Get the physical memory size
#if HAVE_SYSCONF && HAVE__SC_PHYS_PAGES
    long pages = sysconf(_SC_PHYS_PAGES);
    if (pages == -1)
    {
        return false;
    }

    g_totalPhysicalMemSize = (uint64_t)pages * (uint64_t)g_pageSizeUnixInl;
#elif HAVE_SYSCTL
    int mib[2];
    mib[0] = CTL_HW;
    mib[1] = HW_MEMSIZE;
    size_t length = sizeof(INT64);
    int rc = sysctl(mib, 2, &g_totalPhysicalMemSize, &length, NULL, 0);
    if (rc == 0)
    {
        return false;
    }
#else // HAVE_SYSCTL
#error "Don't know how to get total physical memory on this platform"
#endif // HAVE_SYSCTL

    assert(g_totalPhysicalMemSize != 0);

    return true;
}

// Shutdown the interface implementation
void GCToOSInterface::Shutdown()
{
    int ret = munlock(g_helperPage, OS_PAGE_SIZE);
    assert(ret == 0);
    ret = pthread_mutex_destroy(&g_flushProcessWriteBuffersMutex);
    assert(ret == 0);

    munmap(g_helperPage, OS_PAGE_SIZE);

    CleanupCGroup();
}

// Get numeric id of the current thread if possible on the
// current platform. It is intended for logging purposes only.
// Return:
//  Numeric id of the current thread, as best we can retrieve it.
uint64_t GCToOSInterface::GetCurrentThreadIdForLogging()
{
    return (uint64_t)minipal_get_current_thread_id();
}

// Get the process ID of the process.
uint32_t GCToOSInterface::GetCurrentProcessId()
{
    return getpid();
}

// Set ideal processor for the current thread
// Parameters:
//  srcProcNo - processor number the thread currently runs on
//  dstProcNo - processor number the thread should be migrated to
// Return:
//  true if it has succeeded, false if it has failed
bool GCToOSInterface::SetCurrentThreadIdealAffinity(uint16_t srcProcNo, uint16_t dstProcNo)
{
    // There is no way to set a thread ideal processor on Unix, so do nothing.
    return true;
}

// Get the number of the current processor
uint32_t GCToOSInterface::GetCurrentProcessorNumber()
{
#if HAVE_SCHED_GETCPU
    int processorNumber = sched_getcpu();
    assert(processorNumber != -1);
    return processorNumber;
#else
    assert(false); // This method is expected to be called only if CanGetCurrentProcessorNumber is true
    return 0;
#endif
}

// Check if the OS supports getting current processor number
bool GCToOSInterface::CanGetCurrentProcessorNumber()
{
    return HAVE_SCHED_GETCPU;
}

// Flush write buffers of processors that are executing threads of the current process
void GCToOSInterface::FlushProcessWriteBuffers()
{
#ifndef TARGET_WASM
#if defined(__linux__) || HAVE_SYS_MEMBARRIER_H
    if (s_flushUsingMemBarrier)
    {
        int status = membarrier(MEMBARRIER_CMD_PRIVATE_EXPEDITED, 0, 0);
        assert(status == 0 && "Failed to flush using membarrier");
    }
    else
#endif
    if (g_helperPage != 0)
    {
        int status = pthread_mutex_lock(&g_flushProcessWriteBuffersMutex);
        assert(status == 0 && "Failed to lock the flushProcessWriteBuffersMutex lock");

        // Changing a helper memory page protection from read / write to no access
        // causes the OS to issue IPI to flush TLBs on all processors. This also
        // results in flushing the processor buffers.
        status = mprotect(g_helperPage, OS_PAGE_SIZE, PROT_READ | PROT_WRITE);
        assert(status == 0 && "Failed to change helper page protection to read / write");

        // Ensure that the page is dirty before we change the protection so that
        // we prevent the OS from skipping the global TLB flush.
        __sync_add_and_fetch((size_t*)g_helperPage, 1);

        status = mprotect(g_helperPage, OS_PAGE_SIZE, PROT_NONE);
        assert(status == 0 && "Failed to change helper page protection to no access");

        status = pthread_mutex_unlock(&g_flushProcessWriteBuffersMutex);
        assert(status == 0 && "Failed to unlock the flushProcessWriteBuffersMutex lock");
    }
#ifdef TARGET_APPLE
    else
    {
        mach_msg_type_number_t cThreads;
        thread_act_t *pThreads;
        kern_return_t machret = task_threads(mach_task_self(), &pThreads, &cThreads);
        CHECK_MACH("task_threads()", machret);

        uintptr_t sp;
        uintptr_t registerValues[128];

        // Iterate through each of the threads in the list.
        for (mach_msg_type_number_t i = 0; i < cThreads; i++)
        {
            if (__builtin_available (macOS 10.14, iOS 12, tvOS 9, *))
            {
                // Request the threads pointer values to force the thread to emit a memory barrier
                size_t registers = 128;
                machret = thread_get_register_pointer_values(pThreads[i], &sp, &registers, registerValues);
            }
            else
            {
                // fallback implementation for older OS versions
#if defined(HOST_AMD64)
                x86_thread_state64_t threadState;
                mach_msg_type_number_t count = x86_THREAD_STATE64_COUNT;
                machret = thread_get_state(pThreads[i], x86_THREAD_STATE64, (thread_state_t)&threadState, &count);
#elif defined(HOST_ARM64)
                arm_thread_state64_t threadState;
                mach_msg_type_number_t count = ARM_THREAD_STATE64_COUNT;
                machret = thread_get_state(pThreads[i], ARM_THREAD_STATE64, (thread_state_t)&threadState, &count);
#else
                #error Unexpected architecture
#endif
            }

            if (machret == KERN_INSUFFICIENT_BUFFER_SIZE)
            {
                CHECK_MACH("thread_get_register_pointer_values()", machret);
            }

            machret = mach_port_deallocate(mach_task_self(), pThreads[i]);
            CHECK_MACH("mach_port_deallocate()", machret);
        }
        // Deallocate the thread list now we're done with it.
        machret = vm_deallocate(mach_task_self(), (vm_address_t)pThreads, cThreads * sizeof(thread_act_t));
        CHECK_MACH("vm_deallocate()", machret);
    }
#endif // TARGET_APPLE
#endif // !TARGET_WASM
}

// Break into a debugger. Uses a compiler intrinsic if one is available,
// otherwise raises a SIGTRAP.
void GCToOSInterface::DebugBreak()
{
#if __has_builtin(__builtin_debugtrap)
    __builtin_debugtrap();
#else
    raise(SIGTRAP);
#endif
}

// Causes the calling thread to sleep for the specified number of milliseconds
// Parameters:
//  sleepMSec   - time to sleep before switching to another thread
void GCToOSInterface::Sleep(uint32_t sleepMSec)
{
    if (sleepMSec == 0)
    {
        return;
    }

    timespec requested;
    requested.tv_sec = sleepMSec / tccSecondsToMilliSeconds;
    requested.tv_nsec = (sleepMSec - requested.tv_sec * tccSecondsToMilliSeconds) * tccMilliSecondsToNanoSeconds;

    timespec remaining;
    while (nanosleep(&requested, &remaining) == EINTR)
    {
        requested = remaining;
    }
}

// Causes the calling thread to yield execution to another thread that is ready to run on the current processor.
// Parameters:
//  switchCount - number of times the YieldThread was called in a loop
void GCToOSInterface::YieldThread(uint32_t switchCount)
{
    int ret = sched_yield();

    // sched_yield never fails on Linux, unclear about other OSes
    assert(ret == 0);
}

// Reserve virtual memory range.
// Parameters:
//  size       - size of the virtual memory range
//  alignment  - requested memory alignment, 0 means no specific alignment requested
//  flags      - flags to control special settings like write watching
//  committing - memory will be comitted
// Return:
//  Starting virtual address of the reserved range
static void* VirtualReserveInner(size_t size, size_t alignment, uint32_t flags, uint32_t hugePagesFlag, bool committing)
{
    assert(!(flags & VirtualReserveFlags::WriteWatch) && "WriteWatch not supported on Unix");
    if (alignment < OS_PAGE_SIZE)
    {
        alignment = OS_PAGE_SIZE;
    }

    size_t alignedSize = size + (alignment - OS_PAGE_SIZE);
    int mmapFlags = MAP_ANON | MAP_PRIVATE | hugePagesFlag;
#ifdef __HAIKU__
    mmapFlags |= MAP_NORESERVE;
#endif
    void * pRetVal = mmap(nullptr, alignedSize, PROT_NONE, mmapFlags, -1, 0);

    if (pRetVal != MAP_FAILED)
    {
        void * pAlignedRetVal = (void *)(((size_t)pRetVal + (alignment - 1)) & ~(alignment - 1));
        size_t startPadding = (size_t)pAlignedRetVal - (size_t)pRetVal;
        if (startPadding != 0)
        {
            int ret = munmap(pRetVal, startPadding);
            assert(ret == 0);
        }

        size_t endPadding = alignedSize - (startPadding + size);
        if (endPadding != 0)
        {
            int ret = munmap((void *)((size_t)pAlignedRetVal + size), endPadding);
            assert(ret == 0);
        }

        pRetVal = pAlignedRetVal;
#if defined(MADV_DONTDUMP) && !defined(TARGET_WASM)
        // Do not include reserved uncommitted memory in coredump.
        if (!committing)
        {
            madvise(pRetVal, size, MADV_DONTDUMP);
        }
#endif
        return pRetVal;
    }

    return NULL; // return NULL if mmap failed
}

// Reserve virtual memory range.
// Parameters:
//  size      - size of the virtual memory range
//  alignment - requested memory alignment, 0 means no specific alignment requested
//  flags     - flags to control special settings like write watching
//  node      - the NUMA node to reserve memory on
// Return:
//  Starting virtual address of the reserved range
void* GCToOSInterface::VirtualReserve(size_t size, size_t alignment, uint32_t flags, uint16_t node)
{
    return VirtualReserveInner(size, alignment, flags, 0, /* committing */ false);
}

// Release virtual memory range previously reserved using VirtualReserve
// Parameters:
//  address - starting virtual address
//  size    - size of the virtual memory range
// Return:
//  true if it has succeeded, false if it has failed
bool GCToOSInterface::VirtualRelease(void* address, size_t size)
{
    int ret = munmap(address, size);

    return (ret == 0);
}

// Commit virtual memory range. It must be part of a range reserved using VirtualReserve.
// Parameters:
//  address   - starting virtual address
//  size      - size of the virtual memory range
//  newMemory - memory has been newly allocated
// Return:
//  true if it has succeeded, false if it has failed
static bool VirtualCommitInner(void* address, size_t size, uint16_t node, bool newMemory)
{
#ifndef TARGET_WASM
    bool success = mprotect(address, size, PROT_WRITE | PROT_READ) == 0;
#else
    bool success = true;
#endif // !TARGET_WASM

#if defined(MADV_DONTDUMP) && !defined(TARGET_WASM)
    if (success && !newMemory)
    {
        // Include committed memory in coredump. New memory is included by default.
        madvise(address, size, MADV_DODUMP);
    }
#endif

#ifdef TARGET_LINUX
    if (success && g_numaAvailable && (node != NUMA_NODE_UNDEFINED))
    {
        if ((int)node <= g_highestNumaNode)
        {
            int usedNodeMaskBits = g_highestNumaNode + 1;
            int nodeMaskLength = usedNodeMaskBits + sizeof(unsigned long) - 1;
            unsigned long* nodeMask = (unsigned long*)alloca(nodeMaskLength);
            memset(nodeMask, 0, nodeMaskLength);

            int index = node / sizeof(unsigned long);
            nodeMask[index] = ((unsigned long)1) << (node & (sizeof(unsigned long) - 1));

            int st = BindMemoryPolicy(address, size, nodeMask, usedNodeMaskBits);
            assert(st == 0);
            // If the mbind fails, we still return the allocated memory since the node is just a hint
        }
    }
#endif // TARGET_LINUX

    return success;
}

// Commit virtual memory range. It must be part of a range reserved using VirtualReserve.
// Parameters:
//  address - starting virtual address
//  size    - size of the virtual memory range
// Return:
//  true if it has succeeded, false if it has failed
bool GCToOSInterface::VirtualCommit(void* address, size_t size, uint16_t node)
{
    return VirtualCommitInner(address, size, node, /* newMemory */ false);
}

// Commit virtual memory range.
// Parameters:
//  size      - size of the virtual memory range
// Return:
//  Starting virtual address of the committed range
void* GCToOSInterface::VirtualReserveAndCommitLargePages(size_t size, uint16_t node)
{
#if HAVE_MAP_HUGETLB
    uint32_t largePagesFlag = MAP_HUGETLB;
#elif HAVE_VM_FLAGS_SUPERPAGE_SIZE_ANY
    uint32_t largePagesFlag = VM_FLAGS_SUPERPAGE_SIZE_ANY;
#else
    uint32_t largePagesFlag = 0;
#endif

    void* pRetVal = VirtualReserveInner(size, OS_PAGE_SIZE, 0, largePagesFlag, true);
    if (VirtualCommitInner(pRetVal, size, node, /* newMemory */ true))
    {
        return pRetVal;
    }

    return nullptr;
}

// Decomit virtual memory range.
// Parameters:
//  address - starting virtual address
//  size    - size of the virtual memory range
// Return:
//  true if it has succeeded, false if it has failed
bool GCToOSInterface::VirtualDecommit(void* address, size_t size)
{
    // TODO: This can fail, however the GC does not handle the failure gracefully
    // Explicitly calling mmap instead of mprotect here makes it
    // that much more clear to the operating system that we no
    // longer need these pages. Also, GC depends on re-committed pages to
    // be zeroed-out.
    int mmapFlags = MAP_FIXED | MAP_ANON | MAP_PRIVATE;
#ifdef TARGET_HAIKU
    mmapFlags |= MAP_NORESERVE;
#endif
    bool bRetVal = mmap(address, size, PROT_NONE, mmapFlags, -1, 0) != MAP_FAILED;

#ifdef MADV_DONTDUMP
    if (bRetVal)
    {
        // Do not include freed memory in coredump.
        madvise(address, size, MADV_DONTDUMP);
    }
#endif

    return  bRetVal;
}

// Reset virtual memory range. Indicates that data in the memory range specified by address and size is no
// longer of interest, but it should not be decommitted.
// Parameters:
//  address - starting virtual address
//  size    - size of the virtual memory range
//  unlock  - true if the memory range should also be unlocked
// Return:
//  true if it has succeeded, false if it has failed
bool GCToOSInterface::VirtualReset(void * address, size_t size, bool unlock)
{
    int st = EINVAL;

#if defined(MADV_DONTDUMP) || defined(HAVE_MADV_FREE)

    int madviseFlags = 0;

#ifdef MADV_DONTDUMP
    // Do not include reset memory in coredump.
    madviseFlags |= MADV_DONTDUMP;
#endif

#ifdef HAVE_MADV_FREE
    // Tell the kernel that the application doesn't need the pages in the range.
    // Freeing the pages can be delayed until a memory pressure occurs.
    madviseFlags |= MADV_FREE;
#endif

    st = madvise(address, size, madviseFlags);

#endif //defined(MADV_DONTDUMP) || defined(HAVE_MADV_FREE)

#if defined(HAVE_POSIX_MADVISE) && !defined(MADV_DONTDUMP)
    // DONTNEED is the nearest posix equivalent of FREE.
    // Prefer FREE as, since glibc2.6 DONTNEED is a nop.
    st = posix_madvise(address, size, POSIX_MADV_DONTNEED);

#endif //defined(HAVE_POSIX_MADVISE) && !defined(MADV_DONTDUMP)

    return (st == 0);
}

// Check if the OS supports write watching
bool GCToOSInterface::SupportsWriteWatch()
{
    return false;
}

// Reset the write tracking state for the specified virtual memory range.
// Parameters:
//  address - starting virtual address
//  size    - size of the virtual memory range
void GCToOSInterface::ResetWriteWatch(void* address, size_t size)
{
    assert(!"should never call ResetWriteWatch on Unix");
}

// Retrieve addresses of the pages that are written to in a region of virtual memory
// Parameters:
//  resetState         - true indicates to reset the write tracking state
//  address            - starting virtual address
//  size               - size of the virtual memory range
//  pageAddresses      - buffer that receives an array of page addresses in the memory region
//  pageAddressesCount - on input, size of the lpAddresses array, in array elements
//                       on output, the number of page addresses that are returned in the array.
// Return:
//  true if it has succeeded, false if it has failed
bool GCToOSInterface::GetWriteWatch(bool resetState, void* address, size_t size, void** pageAddresses, uintptr_t* pageAddressesCount)
{
    assert(!"should never call GetWriteWatch on Unix");
    return false;
}

bool ReadMemoryValueFromFile(const char* filename, uint64_t* val)
{
    bool result = false;
    char* line = nullptr;
    size_t lineLen = 0;
    char* endptr = nullptr;
    uint64_t num = 0, l, multiplier;
    FILE* file = nullptr;

    if (val == nullptr)
        goto done;

    file = fopen(filename, "r");
    if (file == nullptr)
        goto done;

    if (getline(&line, &lineLen, file) == -1)
        goto done;

    errno = 0;
    num = strtoull(line, &endptr, 0);
    if (line == endptr || errno != 0)
        goto done;

    multiplier = 1;
    switch (*endptr)
    {
    case 'g':
    case 'G': multiplier = 1024;
              FALLTHROUGH;
    case 'm':
    case 'M': multiplier = multiplier * 1024;
              FALLTHROUGH;
    case 'k':
    case 'K': multiplier = multiplier * 1024;
    }

    *val = num * multiplier;
    result = true;
    if (*val / multiplier != num)
        result = false;
done:
    if (file)
        fclose(file);
    free(line);
    return result;
}

static void GetLogicalProcessorCacheSizeFromSysConf(size_t* cacheLevel, size_t* cacheSize)
{
    assert (cacheLevel != nullptr);
    assert (cacheSize != nullptr);

#if defined(_SC_LEVEL1_DCACHE_SIZE) || defined(_SC_LEVEL2_CACHE_SIZE) || defined(_SC_LEVEL3_CACHE_SIZE) || defined(_SC_LEVEL4_CACHE_SIZE)
    const int cacheLevelNames[] =
    {
        _SC_LEVEL1_DCACHE_SIZE,
        _SC_LEVEL2_CACHE_SIZE,
        _SC_LEVEL3_CACHE_SIZE,
        _SC_LEVEL4_CACHE_SIZE,
    };

    for (int i = ARRAY_SIZE(cacheLevelNames) - 1; i >= 0; i--)
    {
        long size = sysconf(cacheLevelNames[i]);
        if (size > 0)
        {
            *cacheSize = (size_t)size;
            *cacheLevel = i + 1;
            break;
        }
    }
#endif
}

static void GetLogicalProcessorCacheSizeFromSysFs(size_t* cacheLevel, size_t* cacheSize)
{
    assert (cacheLevel != nullptr);
    assert (cacheSize != nullptr);

#if defined(TARGET_LINUX) && !defined(HOST_ARM) && !defined(HOST_X86)
    //
    // Retrieve cachesize via sysfs by reading the file /sys/devices/system/cpu/cpu0/cache/index{LastLevelCache}/size
    // for the platform. Currently musl and arm64 should be only cases to use
    // this method to determine cache size.
    //
    size_t level;
    char path_to_size_file[] =  "/sys/devices/system/cpu/cpu0/cache/index-/size";
    char path_to_level_file[] =  "/sys/devices/system/cpu/cpu0/cache/index-/level";
    int index = 40;
    assert(path_to_size_file[index] == '-');
    assert(path_to_level_file[index] == '-');

    for (int i = 0; i < 5; i++)
    {
        path_to_size_file[index] = (char)(48 + i);

        uint64_t cache_size_from_sys_file = 0;

        if (ReadMemoryValueFromFile(path_to_size_file, &cache_size_from_sys_file))
        {
            *cacheSize = std::max(*cacheSize, (size_t)cache_size_from_sys_file);

            path_to_level_file[index] = (char)(48 + i);
            if (ReadMemoryValueFromFile(path_to_level_file, &level))
            {
                *cacheLevel = level;
            }
        }
    }
#endif
}

static void GetLogicalProcessorCacheSizeFromHeuristic(size_t* cacheLevel, size_t* cacheSize)
{
    assert (cacheLevel != nullptr);
    assert (cacheSize != nullptr);

#if (defined(TARGET_LINUX) && !defined(TARGET_APPLE))
    {
        // Use the following heuristics at best depending on the CPU count
        // 1 ~ 4   :  4 MB
        // 5 ~ 16  :  8 MB
        // 17 ~ 64 : 16 MB
        // 65+     : 32 MB
        DWORD logicalCPUs = g_processAffinitySet.Count();
        if (logicalCPUs < 5)
        {
            *cacheSize = 4;
        }
        else if (logicalCPUs < 17)
        {
            *cacheSize = 8;
        }
        else if (logicalCPUs < 65)
        {
            *cacheSize = 16;
        }
        else
        {
            *cacheSize = 32;
        }

        *cacheSize *= (1024 * 1024);
    }
#endif
}

static size_t GetLogicalProcessorCacheSizeFromOS()
{
    size_t cacheLevel = 0;
    size_t cacheSize = 0;

    if (GCConfig::GetGCCacheSizeFromSysConf())
    {
        GetLogicalProcessorCacheSizeFromSysConf(&cacheLevel, &cacheSize);
    }

    if (cacheSize == 0)
    {
        GetLogicalProcessorCacheSizeFromSysFs(&cacheLevel, &cacheSize);
        if (cacheSize == 0)
        {
            GetLogicalProcessorCacheSizeFromHeuristic(&cacheLevel, &cacheSize);
        }
    }

#if HAVE_SYSCTLBYNAME
    if (cacheSize == 0)
    {
        int64_t cacheSizeFromSysctl = 0;
        size_t sz = sizeof(cacheSizeFromSysctl);
        const bool success = false
            // macOS: Since macOS 12.0, Apple added ".perflevelX." to determinate cache sizes for efficiency
            // and performance cores separately. "perflevel0" stands for "performance"
            || sysctlbyname("hw.perflevel0.l3cachesize", &cacheSizeFromSysctl, &sz, nullptr, 0) == 0
            || sysctlbyname("hw.perflevel0.l2cachesize", &cacheSizeFromSysctl, &sz, nullptr, 0) == 0
            // macOS: these report cache sizes for efficiency cores only:
            || sysctlbyname("hw.l3cachesize", &cacheSizeFromSysctl, &sz, nullptr, 0) == 0
            || sysctlbyname("hw.l2cachesize", &cacheSizeFromSysctl, &sz, nullptr, 0) == 0
            || sysctlbyname("hw.l1dcachesize", &cacheSizeFromSysctl, &sz, nullptr, 0) == 0;
        if (success)
        {
            assert(cacheSizeFromSysctl > 0);
            cacheSize = (size_t) cacheSizeFromSysctl;
        }
    }
#endif

#if (defined(HOST_ARM64) || defined(HOST_LOONGARCH64)) && !defined(TARGET_APPLE)
    if (cacheLevel != 3)
    {
        GetLogicalProcessorCacheSizeFromHeuristic(&cacheLevel, &cacheSize);
    }
#endif

    return cacheSize;
}

// Get memory size multiplier based on the passed in units (k = kilo, m = mega, g = giga)
static uint64_t GetMemorySizeMultiplier(char units)
{
    switch(units)
    {
        case 'g':
        case 'G': return 1024 * 1024 * 1024;
        case 'm':
        case 'M': return 1024 * 1024;
        case 'k':
        case 'K': return 1024;
    }

    // No units multiplier
    return 1;
}

#if !defined(__APPLE__) && !defined(__HAIKU__)
// Try to read the MemAvailable entry from /proc/meminfo.
// Return true if the /proc/meminfo existed, the entry was present and we were able to parse it.
static bool ReadMemAvailable(uint64_t* memAvailable)
{
    bool foundMemAvailable = false;
    FILE* memInfoFile = fopen("/proc/meminfo", "r");
    if (memInfoFile != NULL)
    {
        char *line = nullptr;
        size_t lineLen = 0;

        while (getline(&line, &lineLen, memInfoFile) != -1)
        {
            char units = '\0';
            uint64_t available;
            int fieldsParsed = sscanf(line, "MemAvailable: %" SCNu64 " %cB", &available, &units);

            if (fieldsParsed >= 1)
            {
                uint64_t multiplier = GetMemorySizeMultiplier(units);
                *memAvailable = available * multiplier;
                foundMemAvailable = true;
                break;
            }
        }

        free(line);
        fclose(memInfoFile);
    }

    return foundMemAvailable;
}
#endif // !defined(__APPLE__) && !defined(__HAIKU__)

// Get size of the largest cache on the processor die
// Parameters:
//  trueSize - true to return true cache size, false to return scaled up size based on
//             the processor architecture
// Return:
//  Size of the cache
size_t GCToOSInterface::GetCacheSizePerLogicalCpu(bool trueSize)
{
    static volatile size_t s_maxSize;
    static volatile size_t s_maxTrueSize;

    size_t size = trueSize ? s_maxTrueSize : s_maxSize;
    if (size != 0)
        return size;

    size_t maxSize, maxTrueSize;
    maxSize = maxTrueSize = GetLogicalProcessorCacheSizeFromOS(); // Returns the size of the highest level processor cache

    s_maxSize = maxSize;
    s_maxTrueSize = maxTrueSize;

    // printf("GetCacheSizePerLogicalCpu returns %zu, adjusted size %zu\n", maxSize, maxTrueSize);
    return trueSize ? maxTrueSize : maxSize;
}

// Sets the calling thread's affinity to only run on the processor specified
// Parameters:
//  procNo - The requested processor for the calling thread.
// Return:
//  true if setting the affinity was successful, false otherwise.
bool GCToOSInterface::SetThreadAffinity(uint16_t procNo)
{
#if HAVE_SCHED_SETAFFINITY || HAVE_PTHREAD_SETAFFINITY_NP
    cpu_set_t cpuSet;
    CPU_ZERO(&cpuSet);
    CPU_SET((int)procNo, &cpuSet);

    // Snap's default strict confinement does not allow sched_setaffinity(<nonzeroPid>, ...) without manually connecting the
    // process-control plug. sched_setaffinity(<currentThreadPid>, ...) is also currently not allowed, only
    // sched_setaffinity(0, ...). pthread_setaffinity_np(pthread_self(), ...) seems to call
    // sched_setaffinity(<currentThreadPid>, ...) in at least one implementation, and does not work. To work around those
    // issues, use sched_setaffinity(0, ...) if available and only otherwise fall back to pthread_setaffinity_np(). See the
    // following for more information:
    // - https://github.com/dotnet/runtime/pull/38795
    // - https://github.com/dotnet/runtime/issues/1634
    // - https://forum.snapcraft.io/t/requesting-autoconnect-for-interfaces-in-pigmeat-process-control-home/17987/13
#if HAVE_SCHED_SETAFFINITY
    int st = sched_setaffinity(0, sizeof(cpu_set_t), &cpuSet);
#else
    int st = pthread_setaffinity_np(pthread_self(), sizeof(cpu_set_t), &cpuSet);
#endif

    return (st == 0);

#else  // !(HAVE_SCHED_SETAFFINITY || HAVE_PTHREAD_SETAFFINITY_NP)
    // There is no API to manage thread affinity, so let's ignore the request
    return false;
#endif // HAVE_SCHED_SETAFFINITY || HAVE_PTHREAD_SETAFFINITY_NP
}

// Boosts the calling thread's thread priority to a level higher than the default
// for new threads.
// Parameters:
//  None.
// Return:
//  true if the priority boost was successful, false otherwise.
bool GCToOSInterface::BoostThreadPriority()
{
    // [LOCALGC TODO] Thread priority for unix
    return false;
}

// Set the set of processors enabled for GC threads for the current process based on config specified affinity mask and set
// Parameters:
//  configAffinityMask - mask specified by the GCHeapAffinitizeMask config
//  configAffinitySet  - affinity set specified by the GCHeapAffinitizeRanges config
// Return:
//  set of enabled processors
const AffinitySet* GCToOSInterface::SetGCThreadsAffinitySet(uintptr_t configAffinityMask, const AffinitySet* configAffinitySet)
{
    if (!configAffinitySet->IsEmpty())
    {
        // Update the process affinity set using the configured set
        for (size_t i = 0; i < MAX_SUPPORTED_CPUS; i++)
        {
            if (g_processAffinitySet.Contains(i) && !configAffinitySet->Contains(i))
            {
                g_processAffinitySet.Remove(i);
            }
        }
    }

    return &g_processAffinitySet;
}

#if HAVE_PROCFS_STATM
// Return the size of the user-mode portion of the virtual address space of this process.
static size_t GetCurrentVirtualMemorySize()
{
    size_t result = (size_t)-1;
    size_t linelen;
    char* line = nullptr;

    // process virtual memory size is reported in the first column of the /proc/self/statm
    FILE* file = fopen("/proc/self/statm", "r");
    if (file != nullptr && getline(&line, &linelen, file) != -1)
    {
        // The first column of the /proc/self/statm contains the virtual memory size
        char* context = nullptr;
        char* strTok = strtok_r(line, " ", &context);

        errno = 0;
        result = strtoull(strTok, nullptr, 0);
        if (errno == 0)
        {
            long pageSize = sysconf(_SC_PAGE_SIZE);
            if (pageSize != -1)
            {
                result = result * pageSize;
            }
        }
        else
        {
            assert(!"Failed to parse statm file contents.");
            result = (size_t)-1;
        }
    }

    if (file)
        fclose(file);
    free(line);

    return result;
}
#endif // HAVE_PROCFS_STATM

// Return the size of the available user-mode portion of the virtual address space of this process.
// Return:
//  non zero if it has succeeded, GetVirtualMemoryMaxAddress() if not available
size_t GCToOSInterface::GetVirtualMemoryLimit()
{
    rlimit addressSpaceLimit;
    if ((getrlimit(RLIMIT_AS, &addressSpaceLimit) == 0) && (addressSpaceLimit.rlim_cur != RLIM_INFINITY))
    {
        return addressSpaceLimit.rlim_cur;
    }

    // No virtual memory limit
    return GetVirtualMemoryMaxAddress();
}

// Return the maximum address of the of the virtual address space of this process.
// Return:
//  non zero if it has succeeded, 0 if it has failed
size_t GCToOSInterface::GetVirtualMemoryMaxAddress()
{
#ifdef HOST_64BIT
#ifndef TARGET_RISCV64
    // There is no API to get the total virtual address space size on
    // Unix, so we use a constant value representing 128TB, which is
    // the approximate size of total user virtual address space on
    // the currently supported Unix systems.
    static const uint64_t _128TB = (1ull << 47);
    return _128TB;
#else // TARGET_RISCV64
    // For RISC-V Linux Kernel SV39 virtual memory limit is 256gb.
    static const uint64_t _256GB = (1ull << 38);
    return _256GB;
#endif // TARGET_RISCV64
#else
    return (size_t)-1;
#endif
}

// Get the physical memory that this process can use.
// Return:
//  non zero if it has succeeded, 0 if it has failed
// Remarks:
//  If a process runs with a restricted memory limit, it returns the limit. If there's no limit
//  specified, it returns amount of actual physical memory.
uint64_t GCToOSInterface::GetPhysicalMemoryLimit(bool* is_restricted)
{
    size_t restricted_limit;
    if (is_restricted)
        *is_restricted = false;

    restricted_limit = GetRestrictedPhysicalMemoryLimit();
    VolatileStore(&g_RestrictedPhysicalMemoryLimit, restricted_limit);

    if (restricted_limit != 0 && restricted_limit != SIZE_T_MAX)
    {
        if (is_restricted)
            *is_restricted = true;
        return restricted_limit;
    }

    return g_totalPhysicalMemSize;
}

// Get amount of physical memory available for use in the system
uint64_t GetAvailablePhysicalMemory()
{
    uint64_t available = 0;

    // Get the physical memory available.
#if defined(__APPLE__)
    uint32_t mem_free = 0;
    size_t mem_free_length = sizeof(uint32_t);
    assert(g_kern_memorystatus_level_mib != NULL);
    int rc = sysctl(g_kern_memorystatus_level_mib, g_kern_memorystatus_level_mib_length, &mem_free, &mem_free_length, NULL, 0);
    assert(rc == 0);
    if (rc == 0)
    {
        available = (int64_t)mem_free * g_totalPhysicalMemSize / 100;
    }
#elif defined(__FreeBSD__)
    size_t inactive_count = 0, laundry_count = 0, free_count = 0;
    size_t sz = sizeof(inactive_count);
    sysctlbyname("vm.stats.vm.v_inactive_count", &inactive_count, &sz, NULL, 0);

    sz = sizeof(laundry_count);
    sysctlbyname("vm.stats.vm.v_laundry_count", &laundry_count, &sz, NULL, 0);

    sz = sizeof(free_count);
    sysctlbyname("vm.stats.vm.v_free_count", &free_count, &sz, NULL, 0);

    available = (inactive_count + laundry_count + free_count) * sysconf(_SC_PAGESIZE);
#elif defined(__HAIKU__)
    system_info info;
    if (get_system_info(&info) == B_OK)
    {
        available = info.free_memory;
    }
#else // Linux
    static volatile bool tryReadMemInfo = true;

    if (tryReadMemInfo)
    {
        // Ensure that we don't try to read the /proc/meminfo in successive calls to the GetAvailablePhysicalMemory
        // if we have failed to access the file or the file didn't contain the MemAvailable value.
        tryReadMemInfo = ReadMemAvailable(&available);
    }

    if (!tryReadMemInfo)
    {
        // The /proc/meminfo doesn't exist or it doesn't contain the MemAvailable row or the format of the row is invalid
        // Fall back to getting the available pages using sysconf.
        available = sysconf(SYSCONF_PAGES) * sysconf(_SC_PAGE_SIZE);
    }
#endif

    return available;
}

// Get the amount of available swap space
uint64_t GetAvailablePageFile()
{
    uint64_t available = 0;

    int mib[3];
    int rc;

    // Get swap file size
#if HAVE_XSW_USAGE
    // This is available on OSX
    struct xsw_usage xsu;
    mib[0] = CTL_VM;
    mib[1] = VM_SWAPUSAGE;
    size_t length = sizeof(xsu);
    rc = sysctl(mib, 2, &xsu, &length, NULL, 0);
    if (rc == 0)
    {
        available = xsu.xsu_avail;
    }
#elif HAVE_XSWDEV
    // E.g. FreeBSD
    struct xswdev xsw;

    size_t length = 2;
    rc = sysctlnametomib("vm.swap_info", mib, &length);
    if (rc == 0)
    {
        int pagesize = getpagesize();
        // Aggregate the information for all swap files on the system
        for (mib[2] = 0; ; mib[2]++)
        {
            length = sizeof(xsw);
            rc = sysctl(mib, 3, &xsw, &length, NULL, 0);
            if ((rc < 0) || (xsw.xsw_version != XSWDEV_VERSION))
            {
                // All the swap files were processed or coreclr was built against
                // a version of headers not compatible with the current XSWDEV_VERSION.
                break;
            }

            uint64_t avail = xsw.xsw_nblks - xsw.xsw_used;
            available += avail * pagesize;
        }
    }
#elif HAVE_SWAPCTL
    struct anoninfo ai;
    if (swapctl(SC_AINFO, &ai) != -1)
    {
        int pagesize = getpagesize();
        available = ai.ani_free * pagesize;
    }
#elif HAVE_SYSINFO
    // Linux
    struct sysinfo info;
    rc = sysinfo(&info);
    if (rc == 0)
    {
        available = info.freeswap;
#if HAVE_SYSINFO_WITH_MEM_UNIT
        // A newer version of the sysinfo structure represents all the sizes
        // in mem_unit instead of bytes
        available *= info.mem_unit;
#endif // HAVE_SYSINFO_WITH_MEM_UNIT
    }
#endif // HAVE_SYSINFO

    return available;
}

// Get memory status
// Parameters:
//  restricted_limit - The amount of physical memory in bytes that the current process is being restricted to. If non-zero, it used to calculate
//      memory_load and available_physical. If zero, memory_load and available_physical is calculate based on all available memory.
//  memory_load - A number between 0 and 100 that specifies the approximate percentage of physical memory
//      that is in use (0 indicates no memory use and 100 indicates full memory use).
//  available_physical - The amount of physical memory currently available, in bytes.
//  available_page_file - The maximum amount of memory the current process can commit, in bytes.
void GCToOSInterface::GetMemoryStatus(uint64_t restricted_limit, uint32_t* memory_load, uint64_t* available_physical, uint64_t* available_page_file)
{
    uint64_t available = 0;
    uint32_t load = 0;

    size_t used;
    if (restricted_limit != 0)
    {
        // Get the physical memory in use - from it, we can get the physical memory available.
        // We do this only when we have the total physical memory available.
        if (GetPhysicalMemoryUsed(&used))
        {
            available = restricted_limit > used ? restricted_limit - used : 0;
            load = (uint32_t)(((float)used * 100) / (float)restricted_limit);
        }
    }
    else
    {
        available = GetAvailablePhysicalMemory();

        if (memory_load != NULL)
        {
            uint64_t total;
            if (restricted_limit != 0 && restricted_limit != SIZE_T_MAX)
            {
                total = restricted_limit;
            }
            else
            {
                total = g_totalPhysicalMemSize;
            }

            if (total > available)
            {
                used = total - available;
                load = (uint32_t)(((float)used * 100) / (float)total);
            }

#if HAVE_PROCFS_STATM
            rlimit addressSpaceLimit;
            if ((getrlimit(RLIMIT_AS, &addressSpaceLimit) == 0) && (addressSpaceLimit.rlim_cur != RLIM_INFINITY))
            {
                // If there is virtual address space limit set, compute virtual memory load and change
                // the load to this one in case it is higher than the physical memory load
                size_t used_virtual = GetCurrentVirtualMemorySize();
                if (used_virtual != (size_t)-1)
                {
                    uint32_t load_virtual = (uint32_t)(((float)used_virtual * 100) / (float)addressSpaceLimit.rlim_cur);
                    if (load_virtual > load)
                    {
                        load = load_virtual;
                    }
                }
            }
#endif // HAVE_PROCFS_STATM
        }
    }

    if (available_physical != NULL)
        *available_physical = available;

    if (memory_load != nullptr)
        *memory_load = load;

    if (available_page_file != nullptr)
        *available_page_file = GetAvailablePageFile();
}

// Get a high precision performance counter
// Return:
//  The counter value
int64_t GCToOSInterface::QueryPerformanceCounter()
{
    return minipal_hires_ticks();
}

// Get a frequency of the high precision performance counter
// Return:
//  The counter frequency
int64_t GCToOSInterface::QueryPerformanceFrequency()
{
    // The counter frequency of gettimeofday is in microseconds.
    return minipal_hires_tick_frequency();
}

// Get a time stamp with a low precision
// Return:
//  Time stamp in milliseconds
uint64_t GCToOSInterface::GetLowPrecisionTimeStamp()
{
    return (uint64_t)minipal_lowres_ticks();
}

// Gets the total number of processors on the machine, not taking
// into account current process affinity.
// Return:
//  Number of processors on the machine
uint32_t GCToOSInterface::GetTotalProcessorCount()
{
    // Calculated in GCToOSInterface::Initialize using
    // sysconf(_SC_NPROCESSORS_ONLN)
    return g_totalCpuCount;
}

bool GCToOSInterface::CanEnableGCNumaAware()
{
    return g_numaAvailable;
}

bool GCToOSInterface::CanEnableGCCPUGroups()
{
    return false;
}

// Get processor number and optionally its NUMA node number for the specified heap number
// Parameters:
//  heap_number - heap number to get the result for
//  proc_no     - set to the selected processor number
//  node_no     - set to the NUMA node of the selected processor or to NUMA_NODE_UNDEFINED
// Return:
//  true if it succeeded
bool GCToOSInterface::GetProcessorForHeap(uint16_t heap_number, uint16_t* proc_no, uint16_t* node_no)
{
    bool success = false;

    uint16_t availableProcNumber = 0;
    for (size_t procNumber = 0; procNumber < MAX_SUPPORTED_CPUS; procNumber++)
    {
        if (g_processAffinitySet.Contains(procNumber))
        {
            if (availableProcNumber == heap_number)
            {
                *proc_no = procNumber;
#ifdef TARGET_LINUX
                if (GCToOSInterface::CanEnableGCNumaAware())
                {
                    int result = GetNumaNodeNumByCpu(procNumber);
                    *node_no = (result >= 0) ? (uint16_t)result : NUMA_NODE_UNDEFINED;
                }
                else
#endif // TARGET_LINUX
                {
                    *node_no = NUMA_NODE_UNDEFINED;
                }

                success = true;
                break;
            }
            availableProcNumber++;
        }
    }

    return success;
}

// Parse the confing string describing affinitization ranges and update the passed in affinitySet accordingly
// Parameters:
//  config_string - string describing the affinitization range, platform specific
//  start_index  - the range start index extracted from the config_string
//  end_index    - the range end index extracted from the config_string, equal to the start_index if only an index and not a range was passed in
// Return:
//  true if the configString was successfully parsed, false if it was not correct
bool GCToOSInterface::ParseGCHeapAffinitizeRangesEntry(const char** config_string, size_t* start_index, size_t* end_index)
{
    return ParseIndexOrRange(config_string, start_index, end_index);
}
