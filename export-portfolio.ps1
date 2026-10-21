# PowerShell 7. Export only the reviewed portfolio source tree.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$output = Join-Path $root 'artifacts/CSharpServer.zip'
[IO.Directory]::CreateDirectory((Split-Path $output)) | Out-Null
$rootFiles = @('.gitignore','README.md','CSharpServer.slnx','compose.yml','run-tests.bat','build.bat','export-portfolio.ps1')
$sourceFolders = @('docs/','database/','Server/ServerCore/','Server/ServerCore.Tests/','Server/GameServer/','Server/GameServer.Tests/','ServerSmokeTester/')
$toolFiles = @('Server/publish-debug.bat','Server/publish-release.bat','Server/Library/protoc-35.1-win64/bin/GenPackets.bat','Server/Library/protoc-35.1-win64/bin/GenPacketHandlers.ps1','Server/Library/protoc-35.1-win64/bin/Enum.proto','Server/Library/protoc-35.1-win64/bin/Struct.proto','Server/Library/protoc-35.1-win64/bin/Protocol.proto','Server/Library/protoc-35.1-win64/bin/PacketRoutes.json')
$extensions = @('.cs','.csproj','.proto','.json','.md','.svg','.sql','.bat','.ps1')
$files = Get-ChildItem -LiteralPath $root -Recurse -Force -File | Where-Object {
    $relative = [IO.Path]::GetRelativePath($root, $_.FullName).Replace('\','/')
    $inSource = @($sourceFolders | Where-Object { $relative.StartsWith($_, [StringComparison]::Ordinal) }).Count -gt 0
    ($relative -in $rootFiles -or $relative -in $toolFiles -or ($inSource -and $_.Extension -in $extensions)) -and
    $relative -notmatch '(^|/)(\.git|\.vs|bin|obj|artifacts|Build|Publish|Logs|test-results|TestResults|samples)(/|$)' -and
    $_.Name -notmatch '(\.local\.json$|^secrets\.json$|\.log(\.txt)?$)'
}
# The protocol sources and generator scripts intentionally live in a bin directory.
$files = @($files) + @($toolFiles | Where-Object { $_ -match '/bin/' } | ForEach-Object { Get-Item -LiteralPath (Join-Path $root $_) })
$files = $files | Sort-Object -Property FullName -Unique
$stream = [IO.File]::Open($output, [IO.FileMode]::Create)
$archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($root, $file.FullName).Replace('\','/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, "CSharpServer/$relative") | Out-Null
    }
} finally { $archive.Dispose() }
Write-Output "Exported $($files.Count) files to $output"
