// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.Diagnostics.DataContractReader;

public enum DataType
{
    Unknown = 0,

    int8,
    uint8,
    int16,
    uint16,
    int32,
    uint32,
    int64,
    uint64,
    nint,
    nuint,
    pointer,

    GCHandle,
    CodePointer,
    Thread,
    ThreadStore,
    GCAllocContext,
    EEAllocContext,
    Exception,
    ExceptionInfo,
    RuntimeThreadLocals,
    Module,
    ModuleLookupMap,
    AppDomain,
    SystemDomain,
    Assembly,
    LoaderAllocator,
    PEAssembly,
    AssemblyBinder,
    PEImage,
    PEImageLayout,
    CGrowableSymbolStream,
    ProbeExtensionResult,
    MethodTable,
    EEClass,
    ArrayClass,
    MethodTableAuxiliaryData,
    GenericsDictInfo,
    TypeDesc,
    ParamTypeDesc,
    TypeVarTypeDesc,
    FnPtrTypeDesc,
    DynamicMetadata,
    StressLog,
    StressLogModuleDesc,
    StressLogHeader,
    ThreadStressLog,
    StressLogChunk,
    StressMsg,
    StressMsgHeader,
    Object,
    String,
    MethodDesc,
    MethodDescChunk,
    MethodDescCodeData,
    PlatformMetadata,
    PrecodeMachineDescriptor,
    StubPrecodeData,
    FixupPrecodeData,
    ThisPtrRetBufPrecodeData,
    Array,
    SyncBlock,
    SyncTableEntry,
    InteropSyncBlockInfo,
    InstantiatedMethodDesc,
    DynamicMethodDesc,
    StoredSigMethodDesc,
    ArrayMethodDesc,
    FCallMethodDesc,
    PInvokeMethodDesc,
    EEImplMethodDesc,
    CLRToCOMCallMethodDesc,
    RangeSectionMap,
    RangeSectionFragment,
    RangeSection,
    RealCodeHeader,
    CodeHeapListNode,
    MethodDescVersioningState,
    ILCodeVersioningState,
    NativeCodeVersionNode,
    ProfControlBlock,
    ILCodeVersionNode,
    ReadyToRunInfo,
    ReadyToRunHeader,
    ImageDataDirectory,
    RuntimeFunction,
    HashMap,
    Bucket,
    UnwindInfo,
    NonVtableSlot,
    MethodImpl,
    NativeCodeSlot,
    GCCoverageInfo,
    ArrayListBase,
    ArrayListBlock,

    TransitionBlock,
    DebuggerEval,
    ArgumentRegisters,
    CalleeSavedRegisters,
    HijackArgs,

    Frame,
    InlinedCallFrame,
    SoftwareExceptionFrame,
    FramedMethodFrame,
    FuncEvalFrame,
    ResumableFrame,
    FaultingExceptionFrame,
    HijackFrame,
    TailCallFrame,
}
