
macro(append_extra_system_libs NativeLibsExtra)
    if (CLR_CMAKE_TARGET_LINUX AND NOT CLR_CMAKE_TARGET_ANDROID)
        list(APPEND ${NativeLibsExtra} rt)
    elseif (CLR_CMAKE_TARGET_FREEBSD)
        list(APPEND ${NativeLibsExtra} pthread)
        find_library(INOTIFY_LIBRARY inotify HINTS ${CROSS_ROOTFS}/usr/local/lib)
        list(APPEND ${NativeLibsExtra} ${INOTIFY_LIBRARY})
    elseif (CLR_CMAKE_TARGET_OPENBSD)
        list(APPEND ${NativeLibsExtra} pthread)
        find_library(INOTIFY_LIBRARY inotify HINTS ${CROSS_ROOTFS}/usr/local/lib/inotify)
        list(APPEND ${NativeLibsExtra} ${INOTIFY_LIBRARY})
    elseif (CLR_CMAKE_TARGET_SUNOS)
        list(APPEND ${NativeLibsExtra} socket nsl)
    elseif (CLR_CMAKE_TARGET_HAIKU)
        list (APPEND ${NativeLibsExtra} network bsd)
    endif ()

    if (CLR_CMAKE_TARGET_APPLE)
        find_library(FOUNDATION Foundation REQUIRED)
        list(APPEND ${NativeLibsExtra} ${FOUNDATION})
    endif ()

    # See the HAVE_LIBURING_H probe in configure.cmake: only added when liburing's headers and
    # library were both found at configure time, so this is a no-op (and does not fail the
    # build) on any machine/image without liburing-dev installed.
    if (HAVE_LIBURING_H)
        list(APPEND ${NativeLibsExtra} ${LIBURING_LIBRARY})
    endif ()
endmacro()
