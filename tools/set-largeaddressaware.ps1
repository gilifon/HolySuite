# Sets IMAGE_FILE_LARGE_ADDRESS_AWARE (0x0020) in the PE header of a built executable.
#
# WHY THIS EXISTS. HolyLogger ships 32-bit, and Windows hands a 32-bit process only the LOW 2 GB of
# its address space unless the exe declares it can cope with addresses above that mark. That ceiling
# has nothing to do with how much RAM the machine has: operators with big logs were getting
# OutOfMemoryException on PCs with gigabytes free, because RAM was never the limit - the 2 GB range
# of addresses was. Importing a large ADIF wants, at the same moment, the whole file as one UTF-16
# string (twice the file's size, and in ONE unbroken run of addresses), every record parsed into
# QSOs, and the entire log already stored. This flag lifts the ceiling to 4 GB, which also makes an
# unbroken run that size far easier to find.
#
# It is done here, after the compiler has finished, because a C# project has no setting for it - only
# the C++ linker has /LARGEADDRESSAWARE. The usual tool, editbin.exe, comes with the Visual Studio
# C++ workload, which is not installed on the build machine; setting the bit is the whole of what
# editbin does for this flag.
#
# Safe to run twice: a file that already carries the flag is left alone.

param([Parameter(Mandatory = $true, ValueFromRemainingArguments = $true)][string[]] $Path)

$ErrorActionPreference = 'Stop'

$LARGE_ADDRESS_AWARE = 0x0020
$seen = 0

foreach ($file in $Path) {
    if (-not (Test-Path -LiteralPath $file)) { continue }
    $seen++

    $bytes = [System.IO.File]::ReadAllBytes($file)
    if ($bytes.Length -lt 0x40 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) {
        throw "set-largeaddressaware: not an executable (no MZ header): $file"
    }

    # e_lfanew, at 0x3C, points at the PE signature. The COFF file header follows those 4 signature
    # bytes, and Characteristics is 18 bytes into it.
    $peOffset = [System.BitConverter]::ToInt32($bytes, 0x3C)
    if ($peOffset -le 0 -or ($peOffset + 24) -ge $bytes.Length -or
        $bytes[$peOffset] -ne 0x50 -or $bytes[$peOffset + 1] -ne 0x45) {
        throw "set-largeaddressaware: no PE header found: $file"
    }

    $chOffset = $peOffset + 4 + 18
    $before = [System.BitConverter]::ToUInt16($bytes, $chOffset)

    if ($before -band $LARGE_ADDRESS_AWARE) {
        Write-Host ("set-largeaddressaware: already set (0x{0:X4}) - {1}" -f $before, $file)
        continue
    }

    # The flag lives in the low byte, so exactly one bit of the file changes.
    $bytes[$chOffset] = [byte]($bytes[$chOffset] -bor $LARGE_ADDRESS_AWARE)
    [System.IO.File]::WriteAllBytes($file, $bytes)

    # Read it back off the disk. A build must not quietly ship an exe still capped at 2 GB.
    $after = [System.BitConverter]::ToUInt16([System.IO.File]::ReadAllBytes($file), $chOffset)
    if (-not ($after -band $LARGE_ADDRESS_AWARE)) {
        throw "set-largeaddressaware: flag did not stick (0x{0:X4}): $file" -f $after
    }

    Write-Host ("set-largeaddressaware: 0x{0:X4} -> 0x{1:X4} - {2}" -f $before, $after, $file)
}

if ($seen -eq 0) {
    throw "set-largeaddressaware: none of the given files exist: $($Path -join ', ')"
}
