# FileStream 6.0

- [Things to do before starting the rewrite](#things-to-do-before-starting-the-rewrite)
  * [Gather API usage statistics](#gather-api-usage-statistics)
  * [Cooperate](#cooperate)
- [Existing issues](#existing-issues)
  * [Creating FileStream](#creating-filestream)
    + [Add new APIs that allow for opening file for async IO](#add-new-apis-that-allow-for-opening-file-for-async-io)
    + [Avoid unnecessary allocations when using FileStream](#avoid-unnecessary-allocations-when-using-filestream)
    + [Expose FileOptions.NoBuffering flag and support O_DIRECT on Linux](#expose-fileoptionsnobuffering-flag-and-support-o-direct-on-linux)
  * [Async IO](#async-io)
    + [Win32 FileStream will issue a seek on every ReadAsync call](#win32-filestream-will-issue-a-seek-on-every-readasync-call)
    + [Win32 FileStream turns async reads into sync reads](#win32-filestream-turns-async-reads-into-sync-reads)
    + [Asynchronous random file access with FileStream Read vs ReadAsync in a loop / parallel (ReadAsync is slow)](#asynchronous-random-file-access-with-filestream-read-vs-readasync-in-a-loop---parallel--readasync-is-slow-)
    + [FileStream.Windows useAsync WriteAsync calls blocking apis](#filestreamwindows-useasync-writeasync-calls-blocking-apis)
    + [FileStream.FlushAsync ends up doing synchronous writes](#filestreamflushasync-ends-up-doing-synchronous-writes)
    + [Use PreallocatedOverlapped when internal FileStream buffer isn't being used on Windows](#use-preallocatedoverlapped-when-internal-filestream-buffer-isn-t-being-used-on-windows)
    + [Async File IO APIs mimicking Win32 OVERLAPPED](#async-file-io-apis-mimicking-win32-overlapped)
    + [File.ReadAllTextAsync not behaving asynchronously when called on inaccessible network files](#filereadalltextasync-not-behaving-asynchronously-when-called-on-inaccessible-network-files)
    + [FileStream file preallocation performance](#filestream-file-preallocation-performance)
    + [File.WriteAllTextAsync performance issues](#filewritealltextasync-performance-issues)
    + [Cancellation support](#cancellation-support)
  * [Other APIs](#other-apis)
    + [File allocation inconsistency between Windows and Linux and sparse allocations](#file-allocation-inconsistency-between-windows-and-linux-and-sparse-allocations)
    + [Platform-dependent FileStream permissions behavior](#platform-dependent-filestream-permissions-behavior)
    + [System.IO.FileSystem tests failing on FreeBSD](#systemiofilesystem-tests-failing-on-freebsd)
    + [SetCreationTime, SetLastAccessTime, SetLastWriteTime Should not open a new stream to obtain a SafeFileHandle](#setcreationtime--setlastaccesstime--setlastwritetime-should-not-open-a-new-stream-to-obtain-a-safefilehandle)
    + [Support SeBackupPrivilege in System.IO.Filestream API](#support-sebackupprivilege-in-systemiofilestream-api)
- [Priorities](#priorities)
  * [Must have](#must-have)
  * [Great to have](#great-to-have)
  * [Nice to have](#nice-to-have)
  * [Future](#future)
- [Planning](#planning)

## Things to do before starting the rewrite

### Gather API usage statistics

And answer the following questions:

* What is the most common way of creating `FileStream`? `ctor`? `File.Open`? `FileInfo.Open`? `StreamReader` or `StreamWriter` ctor?
* How frequently `isAsync` and `FileOptions.Asynchronous` `FileStream` ctor parameters are used?
* How frequently and in what scenarios `ReadByte` and `WriteByte` are being used?
* In what scenarios our users use `FileStream.Position` and `Seek`?
* Are there any popular types that derive from `FileStream`? What methods do they override an why?
* What users complain about the most? Could be a StackOverflow search.
* Which `FileStream` methods are hot in Azure Profiler?

### Cooperate

* find 1st or 3rd party customers for whom FileStream is important, ask them to contribute benchmarks to the perf repo
* talk to Windows Performance Team and ensure that we follow most recent performance best practices
* talk to Tom and ask whether he would like to implement `IoUring` support

## Existing issues

### Creating FileStream

#### Add new APIs that allow for opening file for async IO 

https://github.com/dotnet/runtime/issues/24698

FileStream exposes a `ctor` that accepts `bool isAsync` and another one for `FileOptions`:

```cs
FileStream(SafeFileHandle handle, FileAccess access, int bufferSize, bool isAsync)
FileStream(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, FileOptions options)
```

`File.Open`, `FileInfo.Open`, `StreamReader` and `StreamWriter` do NOT expose any option to open file for async IO. If we want to improve `FileStream` async support, we should also make it easier to use it.

Solution: add `FileOptions` based overloads ([full list](https://github.com/dotnet/runtime/issues/24698#issuecomment-767631566))

```diff
public partial class StreamReader : System.IO.TextReader
{
    public StreamReader(string path)
+   public StreamReader(string path, System.IO.FileOptions options)
}

public partial class StreamWriter : System.IO.TextWriter
{
    public StreamWriter(string path)
+   public StreamWriter(string path, System.IO.FileOptions options)
}

public static partial class File
{
    public static System.IO.StreamWriter CreateText(string path)
+   public static System.IO.StreamWriter CreateText(string path, System.IO.FileOptions options)
    public static System.IO.FileStream Open(string path, System.IO.FileMode mode)
+   public static System.IO.FileStream Open(string path, System.IO.FileMode mode, System.IO.FileOptions options)
}

public sealed partial class FileInfo : System.IO.FileSystemInfo
{
    public System.IO.FileStream Create()
+   public System.IO.FileStream Create(System.IO.FileOptions options)
}
```

Alternative solution: add static methods with `Async` suffix like `File.OpenAsync(path)`. This would require more work and we don't have async `FileStream` ctors as of today.

We should investigate #25314 first to ensure that we really don't need `OpenAsync`.

#### Avoid unnecessary allocations when using FileStream

https://github.com/dotnet/runtime/issues/15088

When user performs first call to `ReadByte` or `WriteByte`, `FileStream` allocates an array of bytes (`4096` by default, [source code](https://github.com/dotnet/runtime/blob/c9d1fd60d6df9c887a1ec5f645ce57dbc5cd47e1/src/libraries/System.Private.CoreLib/src/System/IO/FileStream.cs#L805)).

```cs
FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
byte firstByte = fs.ReadByte(); // allocates new byte[4096], reads 4096 bytes and returns the first one
byte secondByte = fs.ReadByte(); // reuses previously allocated buffer and returns second byte
```

It happens also when the buffer passed by the user to `Read*|Write*` methods is smaller than the `FileStream` internal buffer ([source code](https://github.com/dotnet/runtime/blob/0c401abd3a8339fffac3e969da4727ceabf8713d/src/libraries/System.Private.CoreLib/src/System/IO/FileStream.Windows.cs#L699)):

```cs
byte[] userBuffer = new byte[512];
FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
fs.Write(userBuffer, 0, userBuffer.Length); // allocates new byte[4096] and writes userBuffer to it, does not perform a syscall
fs.Write(userBuffer, 0, userBuffer.Length); // reuses previously allocated buffer
```

ASP.NET sets it to `1` to avoid large buffer allocations:

```cs
FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 
    bufferSize: 1); // the trick
```

Possible solutions:

* add a `ctor` that accepts `byte[]` (new API required)
* add `FileOptions.PoolBuffer` and reuse buffer size provided in ctor. Return to `ArrayPool` on `FileStream.Dispose`

It can be independent from rewrite as long as we don't decide to remove all buffering logic from strategies and wrap Strategy with `BufferedStream`. This could eliminate a LOT of code duplication.

#### Expose FileOptions.NoBuffering flag and support O_DIRECT on Linux

https://github.com/dotnet/runtime/issues/27408

Every OS has an internal cache for data being read from or written to the file. OS controls when the actual disk operations are being performed and it's transparent to the user code.

Some users would like to have a direct control over that and disable this behaviour. This is typically an anti-pattern, but might be useful when for example writing your own database engine.

Quote from "The Linux Programming Interface" book:

> Direct I/O is sometimes misunderstood as being a means of obtaining fast I/O performance. However, for most applications, using direct I/O can considerably degrade performance. This is because the kernel applies a number of optimizations to improve the performance of I/O done via the buffer cache, including performing sequential read-ahead, performing I/O in clusters of disk blocks, and allowing processes accessing the same file to share buffers in the cache. All of these optimizations are lost when we use direct I/O. Direct I/O is intended only for applications with specialized I/O requirements. For example, database systems that perform their own caching and I/O optimizations don’t need the kernel to consume CPU time and memory performing the same tasks.

Requirements:

* Windows ([source](https://docs.microsoft.com/en-us/windows/win32/fileio/file-buffering#alignment-and-file-access-requirements)):
  * File access sizes, including the optional file offset in the OVERLAPPED structure, if specified, must be for a number of bytes that is an integer **multiple of the volume sector size**. For example, if the sector size is 512 bytes, an application can request reads and writes of 512, 1,024, 1,536, or 2,048 bytes, but not of 335, 981, or 7,171 bytes.
  * File access buffer addresses for read and write operations should be physical sector-aligned, which means aligned on addresses in memory that are integer multiples of the volume's physical sector size. Depending on the disk, this requirement may not be enforced.
* Linux ([source](https://learning.oreilly.com/library/view/the-linux-programming/9781593272203/xhtml/ch13.xhtml)):
  * The data buffer being transferred **must be aligned** on a memory boundary that is a multiple of the block size.
  * The file or device offset at which data transfer commences must be a **multiple of the block size**.
  * The length of the data to be transferred must be a **multiple of the block size**.

> Failure to observe any of these restrictions results in the error EINVAL. In the above list, block size means the physical block size of the device (typically 512 bytes).

Possible solutions:

* implement with full validation on our side (alignment and sizes)
* expose config flags and just add error translations

Problems:

* We would need to implement a cross platform way of getting block size.
* As of today, .NET does not expose an API that allows for allocation of aligned memory:
  * Proposal for `Marshal.Alloc(int cb, int alignment)` ([link](https://github.com/dotnet/runtime/issues/33244))
  * Proposal for `GC.AllocateArray<T>(int length, int generation=-1, bool pinned=false, int alignment=-1)` ([link](https://github.com/dotnet/runtime/issues/27146))
* You can allocate a buffer and get the address of first byte that is X-bytes aligned, but you end up with `Span<byte>` so you can't use the `async` methods. So the best you can do is a sync IO. Is it what the users want?
* All methods that use internal buffering (ReadByte etc) might need to be modified to take this under consideration. Most probably the `BufferedStream` refactor would help a lot here.

Proposal: Explain the limitations and ask for a good justification (what problem is being solved, how lack of buffering is going to help?). Ensure it was not a random idea that turns out to be an anti-pattern in the real life. If good justification is not provided, close the issue as `wontfix`.

### Async IO

#### Win32 FileStream will issue a seek on every ReadAsync call

https://github.com/dotnet/runtime/issues/16354

Windows implementation of file stream does not follow [performance best practices](https://docs.microsoft.com/pl-pl/archive/blogs/winserverperformance/designing-applications-for-high-performance-part-iii-2) and it calls expensive `Seek` every time it performs a `ReadAsync` operation on a seekable file.

```cs
FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 4096, isAsync: true);
await fs.ReadAsync(buffer); // performs Seek
await fs.ReadAsync(buffer); // performs Seek
await fs.ReadAsync(buffer); // performs Seek
```

[Source code](https://github.com/dotnet/runtime/blob/4a8cddb5eec16f546407c85fd1cee9d2c1352041/src/libraries/System.Private.CoreLib/src/System/IO/FileStream.Windows.cs#L879-L884)

File offsets are shared between processes (at least on Linux). When `FileStream.SafeFileHandle` is accessed or when `FileStream` is [created from handle](https://github.com/dotnet/runtime/blob/4a8cddb5eec16f546407c85fd1cee9d2c1352041/src/libraries/System.Private.CoreLib/src/System/IO/FileStream.cs#L145) (not from path), the `_exposedHandle` boolean flag is set to true ([source code](https://github.com/dotnet/runtime/blob/4a8cddb5eec16f546407c85fd1cee9d2c1352041/src/libraries/System.Private.CoreLib/src/System/IO/FileStream.cs#L612)).
When this flag is set, every `FileStream` operation [calls Seek](https://github.com/dotnet/runtime/blob/4a8cddb5eec16f546407c85fd1cee9d2c1352041/src/libraries/System.Private.CoreLib/src/System/IO/FileStream.cs#L641-L648) to ensure that nobody has moved the offset in the meantime. If it was moved by someone else, `FileStream` throws [an exception](https://github.com/dotnet/runtime/blob/4a8cddb5eec16f546407c85fd1cee9d2c1352041/src/libraries/System.Private.CoreLib/src/System/IO/FileStream.cs#L658).

To make things worse, the check can be performed twice for every `AsyncRead`. If `FileStream.SafeFileHandle` was accessed, first time [here](https://github.com/dotnet/runtime/blob/4a8cddb5eec16f546407c85fd1cee9d2c1352041/src/libraries/System.Private.CoreLib/src/System/IO/FileStream.Windows.cs#L856) and second time [here](https://github.com/dotnet/runtime/blob/4a8cddb5eec16f546407c85fd1cee9d2c1352041/src/libraries/System.Private.CoreLib/src/System/IO/FileStream.Windows.cs#L884).

**Getting rid of it will most probably provide the biggest perf gain of the rewrite!!!**

For `ReadAsync` we should just follow Windows best practices, but ensure that the [comment which explains why it was added in the first place](https://github.com/dotnet/runtime/blob/4a8cddb5eec16f546407c85fd1cee9d2c1352041/src/libraries/System.Private.CoreLib/src/System/IO/FileStream.Windows.cs#L879-L883) is **well understood** and we are not missing any edge case scenario support.

When it comes to checks caused by `_exposedHandle` set to true, we can either just remove the checks (breaking change) or use syscalls that accept the offset and don't modify it for others (`pread` and `pwrite` on Linux). This would be also atomic (someone can perform an offset manipulation between seek and read or write). It would also allow for exposing a public method that accepts and offset (see #24847).

#### Win32 FileStream turns async reads into sync reads

https://github.com/dotnet/runtime/issues/16341

> When filling the internal buffer, Win32FileStream does this as part of ReadAsync:

```cs
Task<int> readTask = ReadInternalCoreAsync(_buffer, 0, _bufferSize, 0, cancellationToken);
_readLen = readTask.GetAwaiter().GetResult();
```

[source code](https://github.com/dotnet/runtime/blob/4a8cddb5eec16f546407c85fd1cee9d2c1352041/src/libraries/System.Private.CoreLib/src/System/IO/FileStream.Windows.cs#L795-L796)

>  it appears that when async reads are performed against a Win32FileStream and those reads are smaller than the file stream's buffer, all such reads will be made synchronous, either because they're pulling from the buffer or because they're blocking waiting for the buffer to be filled.

The source code has a [comment](https://github.com/dotnet/runtime/blob/4a8cddb5eec16f546407c85fd1cee9d2c1352041/src/libraries/System.Private.CoreLib/src/System/IO/FileStream.Windows.cs#L782-L791) that explains where this is comming from and it needs to be well understood when fixing the problem.

```cs
byte[] userBuffer = new byte[512];
FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 4096, isAsync: true);
await fs.ReadAsync(userBuffer, 0, userBuffer.Length); // performs the blocking call to fill the internal buffer
```

Dependent on #16354. We could most probably fix it by ALWAYS using `NativeOverlapped`, even for synchronous IO:

> The old DOS SetFilePointer API is an anachronism. One should specify the file offset in the overlapped structure even for synchronous I/O. It should never be necessary to resort to the hack of having private file handles for each thread.

#### Asynchronous random file access with FileStream Read vs ReadAsync in a loop / parallel (ReadAsync is slow)

https://github.com/dotnet/runtime/issues/27047

Reading entire file with `ReadAsync` is few times slower compared to `Read`.

It's very likely caused by an additional `Seek` from #16354. The blocking call from #16341 is also involved as the [StackOverflow question](https://stackoverflow.com/q/51560443/5852046) allocates user buffer of a size smaller that default `FileStream` buffer (1000). 

#16354 should be addressed first, then #16341 and then the repro should be profiled and fixed if there is still a gap.

Dependent on #16354 and #16341.

#### FileStream.Windows useAsync WriteAsync calls blocking apis

https://github.com/dotnet/runtime/issues/25905

> SetEndOfFile in the async path is 50% of the time and it doesn't need to be called (as WriteFile will expand the File).

> SetEndOfFile is also a blocking api itself; so expanding the file using it blocks the thread for as much time (or more) as the async saves blocking.

It looks that every time a write to file would increase it's size, we call a method that extends the file. It's blocking and it's BAD for perf ([source code](https://github.com/dotnet/runtime/blob/4a8cddb5eec16f546407c85fd1cee9d2c1352041/src/libraries/System.Private.CoreLib/src/System/IO/FileStream.Windows.cs#L1076-L1079))

```cs
byte[] userBuffer = new byte[512];
FileStream fileStream = new FileStream(path, FileMode.Create); // creates an empty file

for (int i = 0; i < 10; i++)
{
    await fileStream.WriteAsync(userBuffer, 0, userBuffer.Length); // extends the file before writing bytes, calls SetLengthCore which calls the blocking `SetFileInformationByHandle`
}
```

This need a more deep research, but we should most probably NOT use this method at all and instead provide right arguments for the overlapped structure ([docs](https://docs.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-writefile)) or `WriteFile` method:

> To write to the end of file, specify both the Offset and OffsetHigh members of the OVERLAPPED structure as 0xFFFFFFFF

Important if we decide to use `OVERLAPPED`:

> Considerations for working with synchronous file handles:
> 
>   If lpOverlapped is NULL, the write operation starts at the current file position and WriteFile does not return until the operation is complete, and the system updates the file pointer before WriteFile returns.

>  If lpOverlapped is not NULL, the write operation starts at the offset that is specified in the OVERLAPPED structure and WriteFile does not return until the write operation is complete. The system updates the OVERLAPPED Internal and InternalHigh fields before WriteFile returns.

Can be dependent on #16354 if we decide that we should use `OVERLAPPED` everywhere.

#### FileStream.FlushAsync ends up doing synchronous writes

https://github.com/dotnet/runtime/issues/27643

> This is particularly problematic when the FileStream is configured to use async I/O on Windows, as then FlushWriteBuffer needs to use sync-over-async to do the write, queueing the overlapped I/O and then blocking waiting for the operation to complete... so we have the asynchronous FlushAsync synchronously blocking waiting for the overlapped I/O to complete. Ugh.

If the internal write buffer is not empty, `FlushAsync` writes it do disk in synchronous way.

#### Use PreallocatedOverlapped when internal FileStream buffer isn't being used on Windows

https://github.com/dotnet/runtime/issues/25074

Micro optimization idea, needs profiling and better understanding of the code.

#### Async File IO APIs mimicking Win32 OVERLAPPED

https://github.com/dotnet/runtime/issues/24847

It's basically a request for extending `FileStream` API with methods that accept a file offset.

IMO if we fix #16354, #16341, #27047 and #25905 by using `OVERLAPPED` for both sync and async IO on Windows it should not be hard to implement. On Unix we could use `pread` and `pwrtie` and also give the OS a hint about random access.

#### File.ReadAllTextAsync not behaving asynchronously when called on inaccessible network files

https://github.com/dotnet/runtime/issues/25314

TODO: It needs further triage. From a quick look it seems that `FileStream` ctor is blocking for inaccessible network files. What is the expected behaviour?? Would adding an `FileStream.OpenAsync` method help?

We should investigate it before working on #24698;

#### FileStream file preallocation performance

https://github.com/dotnet/runtime/issues/45946

We don't offer a possibility to create a file with a predefined size (with a single call). We should expose a `FileStream` `ctor` and `File.Create` overloads that allows for that.

When we do that, we should ensure that all our methods that know the size up-front (`File.WriteAllText*`, `File.CopyTo*`) are using these overloads and taking advantage of that. This will allow us to avoid #25905 (expensive file expanding) in framework code.

#### File.WriteAllTextAsync performance issues

https://github.com/dotnet/runtime/issues/23196

On Linux, `File.WriteAllTextAsync` is few times slower than `File.WriteAllText`:

* It's most probably caused by the fact that on Unix, we just perform sync IO on ThreadPool and pretend it to by async. This adds overhead, but we don't know where exactly. We should minimize it.
* The [buffer](https://github.com/dotnet/runtime/blob/3d9bef939ad4edb0aff6cca1c0bcb0e6e7eb3d39/src/libraries/System.IO.FileSystem/src/System/IO/File.cs#L979) used by the method is small (4096 bytes) and it causes a LOT of syscalls and also multiplies the "async" overhead. 
* We should take advantage of knowing the size up-front and specify it when creating the file (#45946).

TODO: Everyone has been guessing so far, we need a proper profiling and investigation

It's independent from `Windows` work items, would be good to address #45946 first.

#### Cancellation support

Multiple user reports:

* https://github.com/dotnet/runtime/issues/1606 "Fully support cancellation on all FileStream operations that take CancellationToken"
* https://github.com/dotnet/runtime/issues/24052 "Linux: unable to cancel async read of /dev/input/input0 file" 
* https://github.com/dotnet/runtime/issues/28583 "proc.StandardOutput.ReadAsync doesn't cancel properly if no output is sent from the process"

We don't support cancellabe File IO on Linux because regular disk files are not supported by epoll ([Source](https://learning.oreilly.com/library/view/the-linux-programming/9781593272203/xhtml/ch04.xhtml)):

> This feature, termed signal-driven I/O, is available only for certain file types, such as terminals, FIFOs, and sockets

TODO: **We should at least investigate the reported Windows issues**, however they might be related to input|output redirection.

We could implement a fully asynchronous File IO on Linux by using [io_uring](https://blogs.oracle.com/linux/an-introduction-to-the-io_uring-asynchronous-io-framework):

* Create a timeout linked to a specific operation in the ring
* Attempt to cancel an operation that is currently in flight

This would be possible with `Strategies` because we could dynamically detect Linux Kernel version and use `io_uring` if available. We could ask Tom for help or include it in 6.0 if we find enough time to implement it on our own.

### Other APIs

#### File allocation inconsistency between Windows and Linux and sparse allocations

https://github.com/dotnet/runtime/issues/29666

```cs
using (var fs = File.Open(fileName, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)) 
{
    fs.SetLength(1024L * 1024L * 1024L * 2);
}
```

> On my ntfs windows 10 machine this creates a 2gb file and decreases the free space reported on the drive by 2gb.

> On my ext4 linux machine with Centos 6.10, the file was created and it shows the correct length but the free space did not decrease. 

TODO: It needs further triage, but from a quick look it seems pre-allocating files currently works in an OS-dependent way.

Algining the behaviours would be a breaking change, which is why we might consider it as part of the rewrite (introduce all breaking changes at once in early preview).

TODO: would it possibly help to write a faster `File.CopyTo` for Unix OSes?

#### Platform-dependent FileStream permissions behavior

https://github.com/dotnet/runtime/issues/24432

TODO: It needs further triage, but from a quick look it seems that we could do a better job when locking a file.

#### System.IO.FileSystem tests failing on FreeBSD

https://github.com/dotnet/runtime/issues/26726

TODO: It needs further triage, but from a quick look it seems that similarly to macOS, FreeBSD does not support file locking.

Should be easy to fix (add a throw for FreeBSD like we do for OSX) or just disable the test on FreeBSD (like we do with OSX), independent from rewrite.

#### SetCreationTime, SetLastAccessTime, SetLastWriteTime Should not open a new stream to obtain a SafeFileHandle

https://github.com/dotnet/runtime/issues/20234

New APIs for things like `GetLastWriteTimeUtc`, `SetLastWriteTimeUtc` etc. The API has been already approved but might be very expensive due to setting `_exposedHandle` to true (https://github.com/dotnet/runtime/issues/20234#issuecomment-584413024). The API most probably needs a revisit.

TODO: verify if the proposed API could be implemented without setting `_exposedHandle` to true.

Should be easy to implement, independent from rewrite but rather low priority.

#### Support SeBackupPrivilege in System.IO.Filestream API

https://github.com/dotnet/runtime/issues/27086

TODO: It needs further triage, but from a quick look it seems that the request is to expose `FILE_FLAG_BACKUP_SEMANTICS ` on Windows. We should find out if Unix offers a similar feature. We should avoid OS-specific features in cross platform .NET so if it's not possible on Unix, then just explain and close it.


## Priorities

Suggested work items (grouped and ordered by context, should be independent from each other):

### Must have

* Gather API usage statistics and get a good understanding of how `FileStream` is used by our customers
* Introduce new abstraction layer and choose file strategy at runtime
* Trully asynchronous File IO on Windows:
  * #16354 Win32 FileStream will issue a seek on every ReadAsync call
  * #16341 Win32 FileStream turns async reads into sync reads
  * #27047 ReadAsync is slow: #16354 and #16341 and some additional profiling
  * #25905 FileStream.Windows useAsync WriteAsync calls blocking apis
  * #27643 FileStream.FlushAsync ends up doing synchronous writes
* File size preallocation:  
  * #29666 File allocation inconsistency between Windows and Linux and sparse allocations (**possible breaking changes**)
  * #45946 FileStream file preallocation performance (**new API**)
  * #23196 File.WriteAllTextAsync performance issues (using the API from above and possibly tuning asyn Unix implementation)
    * add more `File` microbenchmarks (`File.ReadAll*`, `File.WriteAll*`, `File.CopyTo*`, `File.Open`)
* Memory allocations
  * #15088 Avoid unnecessary allocations when using FileStream (**new API**)
  * Memory Profiling
  * Stephen `IValueTaskSource` suggestion
* **Validation of new implementation with Windows Performance Team**
* **Blog post**
* **Best practices doc**
  * check all existing code examples, make sure we recommend the best solutions

### Great to have

* Opening FileStream in async way (user experience improvement):
   * #25314 Do we need `File.OpenAsync`? Do we have a bug or the current behaviour is expected?
   * #24698 Add new APIs that allow for opening file for async IO (**new API**)
     * we should start the API review process as soon as possible
* Trully asynchronous File IO on Windows:
  * #25074 micro optimization suggestion

### Nice to have

* Cancellation support
  * #1606 "Fully support cancellation on all FileStream operations that take CancellationToken"
  * #24052 "Linux: unable to cancel async read of /dev/input/input0 file" 
  * #28583 "proc.StandardOutput.ReadAsync doesn't cancel properly if no output is sent from the process"
* Trully asynchronous File IO on Windows:
  * #24847 feature request (Async File IO APIs mimicking Win32 OVERLAPPED)
* #27408 NoBuffering:
  * very likely to get rejected, but we need to understand customer needs first
  * dependent on #33244 and #27146 which might add APIs for aligned memory allocation

### Future

* #20234 New APIs for SetCreationTime, SetLastAccessTime, SetLastWriteTime etc
* #27086 Support SeBackupPrivilege in System.IO.Filestream API
* Locking:
  * #24432 Platform-dependent FileStream permissions behavior
  * #26726 System.IO.FileSystem tests failing on FreeBSD

## Planning

Work items:

* [ ] API statistics
  * [ ] Azure Profiler
  * [ ] nuget.org
  * [ ] GitHub
  * [ ] StackOverflow
* [ ] Cooperation
  * [ ] Windows Performance Team
  * [ ] Benchmarks from Partners
    * [ ] Reach Out
    * [ ] Review PRs
* [ ] Introducing new abstraction layer (#47128)
  * [ ] Review PR
  * [ ] Address PR feedback
* [ ] Async File IO on Windows
  * [ ] #16354 (Seek)
  * [ ] #16341 async reads are sync
  * [ ] #27047 ReadAsync is slow
  * [ ] #25905 WriteAsync calls blocking resize
  * [ ] #27643 FlushAsync calls sync writes
* [ ] File size preallocation
  * [ ] #29666 File allocation inconsistency
  * [ ] #45946 File preallocation performance
  * [ ] #23196 File.WriteAllTextAsync (Linux)
* [ ] Memory Allocations
  * [ ] Memory Profiling
    * [ ] Read* code-path (top X based on stats)
    * [ ] Write* code-path (top X based on stats)
    * [ ] Other code paths (top X based on stats)
  * [ ] #15088 Avoid unnecessary allocations
  * [ ] Stephen `IValueTaskSource` suggestion

Dependencies:
* Getting API statistics and Cooperation are independent from actual coding.
* Async File IO on Windows is dependent on the introduction of new abstraction layer (#47128). Each of it's work items are listed above in the dependency order
* File size preallocation should be independent from `Strategy`
* Memory Allocations should also be independent from `Strategy`, but it would be better to start the analysis after #47128 is merged.
