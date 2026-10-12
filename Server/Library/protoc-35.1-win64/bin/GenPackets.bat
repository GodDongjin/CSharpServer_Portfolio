@echo off
setlocal
pushd "%~dp0"
set "PROTOCOL_DIR=..\..\..\GameServer\Protocol"
set "PACKET_DIR=..\..\..\GameServer\Packet"

if not exist "protoc.exe" (
    echo [ERROR] protoc.exe is missing. Place the protoc compiler beside this script.
    popd
    exit /b 1
)

protoc.exe --proto_path="." --csharp_out="%PROTOCOL_DIR%" "Enum.proto" "Struct.proto" "Protocol.proto"
if errorlevel 1 goto failed

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "GenPacketHandlers.ps1" -ProtoPath "Protocol.proto" -RoutesPath "PacketRoutes.json" -PacketIdsPath "%PROTOCOL_DIR%\PacketIds.cs" -HandlerPath "%PACKET_DIR%\GSPacketHandler.cs" -PacketPath "%PACKET_DIR%\GSPacket.cs"
if errorlevel 1 goto failed

echo [SUCCESS] Protocol and packet handlers generated.
popd
exit /b 0

:failed
echo [ERROR] Packet generation failed.
popd
exit /b 1
