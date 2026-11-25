# Starts the six processes in separate windows: API, Broker, FIX gateway, SimCorp, Bridge, then Calypso.
$root = Split-Path $PSScriptRoot -Parent
dotnet build "$root\TradeFlow.slnx" -c Debug
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$projects = @(
    "src\TradeFlow.Api",
    "src\TradeFlow.BrokerSimulator",
    "src\TradeFlow.FixGateway",
    "src\TradeFlow.SimCorpSimulator",
    "src\TradeFlow.Bridge",
    "src\TradeFlow.CalypsoSimulator"
)
foreach ($p in $projects) {
    Start-Process powershell -WorkingDirectory "$root\$p" -ArgumentList "-NoExit", "-Command", "`$host.UI.RawUI.WindowTitle='$p'; dotnet run --no-build"
    Start-Sleep -Seconds 4
}
Write-Host "Running. Reconciliation: http://localhost:5080/api/reconciliation"
