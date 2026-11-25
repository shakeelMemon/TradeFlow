# Run in an elevated (Administrator) PowerShell. Turns on Windows' built-in MSMQ server.
Enable-WindowsOptionalFeature -Online -FeatureName MSMQ-Server -All -NoRestart
Get-Service MSMQ | Select-Object Name, Status
Write-Host "If MSMQ isn't Running, restart Windows. If the feature isn't available on this PC, set Legacy:Transport to 'File' in tradeflow.shared.json."
