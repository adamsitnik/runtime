// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

/*=====================================================================
**
** Source:  ReadFile.c (test 2)
**
** Purpose: Tests the PAL implementation of the ReadFile function.
**          Creates a test file and performs an array of read tests.
**
** Assumes successful:
**          CreateFile
**          CloseHandle
**          WriteFile
**          GetLastError
**
**===================================================================*/


#include <palsuite.h>

#define szStringTest "The quick fox jumped over the lazy dog's back.\0"
#define szEmptyString ""
#define szReadableFile "Readable.txt"
#define szResultsFile "Results.txt"

//Previously number of tests was 6, now 4 refer VSW 312690
#define NOOFTESTS 4

char *readBuffer_ReadFile_test2;

BOOL validateResults_ReadFile_test2(const char* szString,  // string read
                     DWORD dwByteCount,     // amount requested
                     DWORD dwBytesRead)     // amount read
{
    // were the correct number of bytes read?
    if (dwBytesRead > dwByteCount)
    {
        Trace("bytes read > bytes asked for\n");
        return FALSE;
    }
    if (dwBytesRead != strlen(szString))
    {
        Trace("bytes read != length of read string\n");
        return FALSE;
    }

    //
    // compare results
    //

    if (memcmp(szString, szStringTest, dwBytesRead) != 0)
    {
        Trace("read = %s  string = %s", szString, szStringTest);
        return FALSE;
    }

    return TRUE;
}

BOOL readTest_ReadFile_test2(DWORD dwByteCount, char cResult)
{
    HANDLE hFile = NULL;
    DWORD dwBytesRead;
    BOOL bRc = FALSE;

    // open the test file
    hFile = CreateFile(szReadableFile,
        GENERIC_READ,
        FILE_SHARE_READ,
        NULL,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        NULL);
    if(hFile == INVALID_HANDLE_VALUE)
    {
        Trace("ReadFile: ERROR -> Unable to open file \"%s\".\n",
            szReadableFile);
        return FALSE;
    }

    SYSTEM_INFO sysInfo;
    GetSystemInfo(&sysInfo);
    long pageSize = sysInfo.dwPageSize;
    memset(readBuffer_ReadFile_test2, 0, pageSize);

    bRc = ReadFile(hFile, readBuffer_ReadFile_test2, dwByteCount, &dwBytesRead, NULL);

    if (bRc == FALSE)
    {
        // if it failed, was it supposed to fail?
        if (cResult == '1')
        {
            Trace("\nbRc = %d\n", bRc);
            Trace("readBuffer = [%s]  dwByteCount = %d  dwBytesRead = %d\n", readBuffer_ReadFile_test2, dwByteCount, dwBytesRead);
            Trace("cresult = 1\n");
            Trace("getlasterror = %d\n", GetLastError());
            CloseHandle(hFile);
            return FALSE;
        }
    }
    else
    {
        CloseHandle(hFile);
        // if it passed, was it supposed to pass?
        if (cResult == '0')
        {
            Trace("cresult = 0\n");
            return FALSE;
        }
        else
        {
            return (validateResults_ReadFile_test2(readBuffer_ReadFile_test2, dwByteCount, dwBytesRead));
        }
    }

    CloseHandle(hFile);
    return TRUE;
}

PALTEST(file_io_ReadFile_test2_paltest_readfile_test2, "file_io/ReadFile/test2/paltest_readfile_test2")
{
    HANDLE hFile = NULL;

    SYSTEM_INFO sysInfo;
    GetSystemInfo(&sysInfo);
    long pageSize = sysInfo.dwPageSize;
    long bufferSize = 2 * pageSize;

    DWORD dwByteCount[] = { 0,
                            10,
                            (DWORD)strlen(szStringTest),
                            (DWORD)pageSize
    // Commented out two negative test cases : Refer VSW 312690
    //                            2 * pageSize,
    //                           -1
                            };

    DWORD oldProt;
    char szResults[] =  "1111"; // Was "111100": Refer VSW 312690
    int i;
    BOOL bRc = FALSE;
    DWORD dwBytesWritten = 0;

    if (0 != PAL_Initialize(argc,argv))
    {
        return FAIL;
    }

    /* allocate read-write memery for readBuffer */
    if (!(readBuffer_ReadFile_test2 = (char*) VirtualAlloc(NULL, bufferSize, MEM_COMMIT, PAGE_READWRITE)))
	{
		Fail("VirtualAlloc failed: GetLastError returns %d\n", GetLastError());
		return FAIL;
	}

    /* write protect the second page of readBuffer */
	if (!VirtualProtect(&readBuffer_ReadFile_test2[pageSize], pageSize, PAGE_NOACCESS, &oldProt))
	{
		Fail("VirtualProtect failed: GetLastError returns %d\n", GetLastError());
		return FAIL;
	}

    // create the test file
    hFile = CreateFile(szReadableFile,
        GENERIC_WRITE,
        FILE_SHARE_WRITE,
        NULL,
        CREATE_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        NULL);

	if(hFile == INVALID_HANDLE_VALUE)
    {
        Fail("ReadFile: ERROR -> Unable to create file \"%s\" (%d).\n",
             szReadableFile, GetLastError());
    }

    bRc = WriteFile(hFile, szStringTest, strlen(szStringTest), &dwBytesWritten, NULL);
    CloseHandle(hFile);


    for (i = 0; i< NOOFTESTS; i++)
    {
        bRc = readTest_ReadFile_test2(dwByteCount[i], szResults[i]);
        if (bRc != TRUE)
        {
            Fail("ReadFile: ERROR -> Failed on test[%d]\n", i);
        }
    }

	VirtualFree(readBuffer_ReadFile_test2, bufferSize, MEM_RELEASE);
    PAL_Terminate();
    return PASS;
}
