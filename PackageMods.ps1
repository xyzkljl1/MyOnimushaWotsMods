[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = $PSScriptRoot

function Get-IniValue {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $Key
    )

    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -match '^\s*[;#]') {
            continue
        }

        if ($line -match '^\s*([^=]+?)\s*=\s*(.*?)\s*$' -and $Matches[1] -ieq $Key) {
            return $Matches[2]
        }
    }

    throw "Missing '$Key' in $Path"
}

function ConvertTo-SafeFileNamePart {
    param(
        [Parameter(Mandatory)]
        [string] $Value
    )

    $invalidCharacters = [IO.Path]::GetInvalidFileNameChars()
    $escapedCharacters = [Regex]::Escape((-join $invalidCharacters))
    $safeValue = ($Value -replace "[$escapedCharacters]", '_').Trim().TrimEnd('.')

    if ([string]::IsNullOrWhiteSpace($safeValue)) {
        throw "The value '$Value' cannot be converted to a valid file-name component."
    }

    return $safeValue
}

$sevenZipCommand = $null
foreach ($candidate in @('7z', '7zz', '7za')) {
    $command = Get-Command $candidate -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        $sevenZipCommand = $command.Source
        break
    }
}

if ($null -eq $sevenZipCommand) {
    throw "7-Zip was not found. Install 7-Zip and ensure 7z, 7zz, or 7za is available on PATH."
}

$mods = @(
    Get-ChildItem -LiteralPath $repositoryRoot -Directory |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'modinfo.ini') }
)

if ($mods.Count -eq 0) {
    throw "No mod directories containing modinfo.ini were found in $repositoryRoot"
}

# Archives are generated artifacts. Remove only root-level .7z files.
foreach ($oldArchive in Get-ChildItem -LiteralPath $repositoryRoot -File -Filter '*.7z') {
    if ([IO.Path]::GetFullPath($oldArchive.DirectoryName) -ne [IO.Path]::GetFullPath($repositoryRoot)) {
        throw "Refusing to delete an archive outside the repository root: $($oldArchive.FullName)"
    }

    Remove-Item -LiteralPath $oldArchive.FullName -Force
    Write-Host "Removed old package: $($oldArchive.Name)"
}

foreach ($mod in $mods) {
    $modInfoPath = Join-Path $mod.FullName 'modinfo.ini'
    $modName = ConvertTo-SafeFileNamePart (Get-IniValue -Path $modInfoPath -Key 'name')
    $modVersion = ConvertTo-SafeFileNamePart (Get-IniValue -Path $modInfoPath -Key 'version')
    $archiveName = "$modName-$modVersion.7z"
    $archivePath = Join-Path $repositoryRoot $archiveName
    $reframeworkPath = Join-Path $mod.FullName 'reframework'

    if (-not (Test-Path -LiteralPath $reframeworkPath -PathType Container)) {
        throw "Missing reframework directory in mod: $($mod.FullName)"
    }

    Push-Location $mod.FullName
    try {
        & $sevenZipCommand a -t7z -mx=9 -y $archivePath 'modinfo.ini' 'reframework'
        if ($LASTEXITCODE -ne 0) {
            throw "7-Zip failed for $($mod.Name) with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }

    Write-Host "Created package: $archiveName"
}
