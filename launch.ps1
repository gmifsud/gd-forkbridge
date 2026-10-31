cd D:\Repos\Tools\gd-forkbridge
$env:FORKBRIDGE_ALIGNMENT_CONFIG = "D:\ForkBridge\state\alignment\forkbridge.alignment.yml"
dotnet run --project src\ForkBridge.Web -- --config "$PWD\src\ForkBridge.Web\forkbridge.yml"