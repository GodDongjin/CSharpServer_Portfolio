# 기존 구현은 보존하고 자동 생성 구간과 새 핸들러만 갱신한다.
# 패킷 ID, 메시지, 핸들러 이름은 PacketRoutes.json을 기준으로 생성한다.
param(
    [string]$ProtoPath,
    [string]$HandlerPath,
    [string]$PacketPath,
    [string]$RoutesPath,
    [string]$PacketIdsPath,
    [string]$SessionType = "GameSession",
    [string]$AccessModifier = "public",
    [string]$HandledPrefixes = "REQ_",
    [ValidateSet("Server", "Client")]
    [string]$StubStyle = "Server"
)

$ErrorActionPreference = "Stop"

function Get-ProtoMessages([string]$path) {
    $text = Get-Content -Raw -Path $path -Encoding UTF8
    $ms = [regex]::Matches($text, 'message\s+(\w+)\s*\{')
    return $ms | ForEach-Object { $_.Groups[1].Value }
}

function ToPascalHandlerName([string]$messageName) {
    $parts = $messageName.Split('_') | Where-Object { $_.Length -gt 0 } | ForEach-Object {
        $_.Substring(0,1).ToUpper() + $_.Substring(1).ToLower()
    }
    return "Handle" + ($parts -join '')
}

# text에서 startIndex(여는 중괄호 '{'의 위치)와 짝이 맞는 닫는 중괄호 '}'의 인덱스를 찾는다.
# 중첩된 중괄호(메서드 안의 if/for 블록 등)를 올바르게 건너뛰기 위해 깊이 카운팅 방식을 사용한다.
function Find-MatchingBraceEnd([string]$text, [int]$openBraceIndex) {
    $depth = 0
    for ($i = $openBraceIndex; $i -lt $text.Length; $i++) {
        $ch = $text[$i]
        if ($ch -eq '{') { $depth++ }
        elseif ($ch -eq '}') {
            $depth--
            if ($depth -eq 0) { return $i }
        }
    }
    throw "짝이 맞는 중괄호를 찾지 못했습니다. (index=$openBraceIndex)"
}

# headerPattern으로 "이름 { " 부분을 찾고, 그 블록의 [본문 시작 인덱스, 본문 끝 인덱스(닫는 중괄호 전)]를 반환한다.
function Get-BlockRange([string]$text, [string]$headerPattern, [string]$errorLabel) {
    $headerMatch = [regex]::Match($text, $headerPattern)
    if (-not $headerMatch.Success) { throw "$errorLabel 을(를) 찾을 수 없습니다." }

    $openBraceIndex = $headerMatch.Index + $headerMatch.Length - 1
    $closeBraceIndex = Find-MatchingBraceEnd $text $openBraceIndex

    return @{
        BodyStart = $openBraceIndex + 1
        BodyEnd = $closeBraceIndex
    }
}

# 블록의 [본문 시작, 본문 끝) 범위에서 "실제 내용이 끝나는 위치"(트레일링 공백 직전)를 계산한다.
function Get-ContentEnd([string]$text, [int]$bodyStart, [int]$bodyEnd) {
    $body = $text.Substring($bodyStart, $bodyEnd - $bodyStart)
    $trimmed = $body.TrimEnd()
    return $bodyStart + $trimmed.Length
}

$messages = Get-ProtoMessages $ProtoPath
if ($messages.Count -eq 0) {
    Write-Output "Protocol.proto에서 메시지를 찾지 못했습니다."
    exit 0
}

# 메시지 순서와 무관하게 기존 통신 ID를 유지한다.
$routes = Get-Content -Raw -LiteralPath $RoutesPath -Encoding UTF8 | ConvertFrom-Json
if ($routes.Count -eq 0) { throw "패킷 정의가 비어 있습니다." }
foreach ($field in @("Id", "Name", "Constant")) {
    if (@($routes | Group-Object -Property $field | Where-Object Count -gt 1).Count -gt 0) {
        throw "중복된 패킷 정의: $field"
    }
}
foreach ($route in $routes) {
    if ($route.Id -lt 0 -or $route.Id -gt 65535 -or $route.Id -ne [int]$route.Id) { throw "잘못된 패킷 ID" }
    if ($messages -notcontains $route.Message) { throw "메시지가 없습니다: $($route.Message)" }
    foreach ($name in @($route.Name, $route.Message, $route.Constant)) {
        if ($name -cnotmatch '^[A-Za-z_][A-Za-z0-9_]*$') { throw "잘못된 식별자: $name" }
    }
    if ($route.Handler -and $route.Handler -cnotmatch '^Handle[A-Za-z0-9_]+$') { throw "잘못된 핸들러 이름" }
}

$handlerText = Get-Content -Raw -Path $HandlerPath -Encoding UTF8
$packetText = Get-Content -Raw -Path $PacketPath -Encoding UTF8

# --- GSPacketHandler.cs: PACKET_ID enum을 .proto 기준으로 완전히 재생성 ---
$enumLines = $routes | ForEach-Object { "        PKT_$($_.Name) = $($_.Id)," }
$newEnumBody = "`r`n" + ($enumLines -join "`r`n") + "`r`n    "

$enumRange = Get-BlockRange $handlerText '(?:public|internal) enum PACKET_ID : ushort\s*\{' "PACKET_ID enum"
$handlerText = $handlerText.Remove($enumRange.BodyStart, $enumRange.BodyEnd - $enumRange.BodyStart).Insert($enumRange.BodyStart, $newEnumBody)

# --- GSPacketHandler.cs: Initialize()의 Register 목록을 .proto 기준(HandledPrefixes에 해당하는 것만)으로 완전히 재생성 ---
# 서버는 REQ_*(클라이언트가 보내는 요청)를, 클라이언트는 ACK_*/SEND_*(서버가 보내는 응답/푸시)를 처리해야 하므로 대상이 다르다.
# Handler가 없는 응답 및 직접 처리 Pong은 일반 작업 큐에 등록하지 않는다.
$reqMessages = @($routes | Where-Object { $_.Handler })
$registerLines = $reqMessages | ForEach-Object {
    "            Register(PACKET_ID.PKT_$($_.Name), $($_.Message).Parser, _packet.$($_.Handler));"
}
$newRegisteredContent = "            // 패킷 ID에 대응하는 처리 함수를 등록한다.`r`n" + ($registerLines -join "`r`n") + "`r`n"

# Initialize() 안에는 null 체크 등 손으로 추가한 코드가 있을 수 있으므로,
# 메서드 전체가 아니라 // AUTO-GENERATED-BEGIN ~ // AUTO-GENERATED-END 사이만 재생성한다.
$markerPattern = '(?<begin>[ \t]*// AUTO-GENERATED-BEGIN\r?\n)(?<content>.*?)(?<end>[ \t]*// AUTO-GENERATED-END)'
$markerMatch = [regex]::Match($handlerText, $markerPattern, 'Singleline')
if (-not $markerMatch.Success) {
    throw "Initialize() 안에서 // AUTO-GENERATED-BEGIN / // AUTO-GENERATED-END 마커를 찾을 수 없습니다. Register 호출부를 이 마커로 감싸주세요."
}
$contentGroup = $markerMatch.Groups['content']
$handlerText = $handlerText.Remove($contentGroup.Index, $contentGroup.Length).Insert($contentGroup.Index, $newRegisteredContent)

# --- GSPacket.cs: 아직 없는 핸들러 메서드만 "추가" (기존 구현은 절대 건드리지 않음) ---
# 같은 폴더의 GSPacket.*.cs에 분리된 구현도 확인한다. 새 스텁은 기본 파일에만 추가한다.
$packetSources = @($packetText)
$packetDirectory = Split-Path -Parent (Resolve-Path -LiteralPath $PacketPath).Path
$packetStem = [System.IO.Path]::GetFileNameWithoutExtension($PacketPath)
foreach ($partialFile in Get-ChildItem -LiteralPath $packetDirectory -Filter "$packetStem.*.cs" -File) {
    $partialText = Get-Content -Raw -LiteralPath $partialFile.FullName -Encoding UTF8
    if ($partialText -match '\bpartial\s+class\s+GSPacket\b') {
        $packetSources += $partialText
    }
}
$existingHandlerMethods = $packetSources | ForEach-Object {
    [regex]::Matches($_, '\bHandle(\w+)\s*\(') | ForEach-Object { "Handle" + $_.Groups[1].Value }
}
$newHandlerMethods = New-Object System.Collections.Generic.List[string]

foreach ($route in $reqMessages) {
    $msg = $route.Message
    $handlerName = $route.Handler
    if ($existingHandlerMethods -notcontains $handlerName) {
        if ($StubStyle -eq "Client") {
            $newHandlerMethods.Add(@"
        $AccessModifier void $handlerName($SessionType session, $msg packet)
        {
            _handler.OnAckPacket(packet);
        }
"@)
        }
        else {
            $newHandlerMethods.Add(@"
        $AccessModifier async ValueTask<bool> $handlerName($SessionType session, $msg packet)
        {
            return true;
        }
"@)
        }
    }
}

if ($newHandlerMethods.Count -gt 0) {
    $classRange = Get-BlockRange $packetText '(?:public|internal)\s+sealed\s+(?:partial\s+)?class\s+GSPacket\s*\{' "GSPacket 클래스"
    $insertPos = Get-ContentEnd $packetText $classRange.BodyStart $classRange.BodyEnd
    $insertion = "`r`n`r`n" + ($newHandlerMethods -join "`r`n`r`n")
    $packetText = $packetText.Insert($insertPos, $insertion)
}

$constantLines = $routes | ForEach-Object { "        public const UInt16 $($_.Constant) = $($_.Id);" }
$idsText = @"
// 자동 생성 파일. PacketRoutes.json을 수정하고 GenPackets.bat를 실행한다.
using System;

namespace CSharp.Protocol
{
    public static class PacketIds
    {
$($constantLines -join "`r`n")
    }
}
"@

Set-Content -LiteralPath $HandlerPath -Value $handlerText -NoNewline -Encoding UTF8
Set-Content -LiteralPath $PacketPath -Value $packetText -NoNewline -Encoding UTF8
Set-Content -LiteralPath $PacketIdsPath -Value $idsText -NoNewline -Encoding UTF8
Write-Output "PACKET_ID: $($routes.Count), Register: $($reqMessages.Count), New handlers: $($newHandlerMethods.Count)"
