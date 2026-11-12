# Builds storm.dll (StormLib, MIT) from a pinned commit and puts it where the
# launcher project embeds it from. Needs git, CMake and Visual Studio's C++
# tools. Without it the launcher still builds; it just can't play the
# client's login music.
$ErrorActionPreference = 'Stop'

$commit = '116135c27e8c0dcf8ea0a0c549a6d737ce10fc73'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$source = Join-Path $root 'obj/stormlib'
$build = Join-Path $source 'build'
$target = Join-Path $root 'src/MoPLauncher/native'

function Run([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed ($LASTEXITCODE)" }
}

if (-not (Test-Path (Join-Path $source '.git'))) {
    New-Item -ItemType Directory -Force $source | Out-Null
    Run 'git init' { git init -q $source }
    Run 'git remote add' { git -C $source remote add origin https://github.com/ladislav-zezula/StormLib }
}
Run 'git fetch' { git -C $source fetch -q --depth 1 origin $commit }
Run 'git checkout' { git -C $source checkout -q FETCH_HEAD }

# Unicode paths, and the C runtime linked in so players need nothing installed.
Run 'cmake configure' {
    cmake -S $source -B $build -A x64 `
        -DBUILD_SHARED_LIBS=ON -DSTORM_UNICODE=ON -DSTORM_SKIP_INSTALL=ON `
        -DSTORM_USE_BUNDLED_LIBRARIES=ON `
        -DCMAKE_POLICY_DEFAULT_CMP0091=NEW -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded
}
Run 'cmake build' { cmake --build $build --config Release }

$dll = Get-ChildItem $build -Recurse -Filter *.dll |
    Where-Object { $_.Name -match '^(storm|StormLib)\.dll$' -and $_.FullName -match 'Release' } |
    Select-Object -First 1
if (-not $dll) { throw 'The StormLib build produced no DLL.' }

New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item $dll.FullName (Join-Path $target 'storm.dll') -Force
Write-Host "Built $($dll.Name) -> src/MoPLauncher/native/storm.dll ($($dll.Length) bytes)"
