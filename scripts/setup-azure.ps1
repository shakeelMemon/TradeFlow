# Creates the Azure Service Bus topic + subscription TradeFlow uses (Standard tier is needed for topics).
# Requires: az login. Afterwards set the printed connection string as an environment variable.
param(
    [string]$ResourceGroup = "rg-tradeflow",
    [string]$Location = "canadacentral",
    [string]$Namespace = "sb-tradeflow-$((Get-Random -Maximum 99999))"
)

az group create -n $ResourceGroup -l $Location | Out-Null
az servicebus namespace create -g $ResourceGroup -n $Namespace -l $Location --sku Standard | Out-Null
az servicebus topic create -g $ResourceGroup --namespace-name $Namespace -n trade-orders `
    --enable-duplicate-detection true --duplicate-detection-history-time-window PT10M | Out-Null
az servicebus topic subscription create -g $ResourceGroup --namespace-name $Namespace --topic-name trade-orders `
    -n simcorp --max-delivery-count 5 --enable-dead-lettering-on-message-expiration true | Out-Null

$cs = az servicebus namespace authorization-rule keys list -g $ResourceGroup --namespace-name $Namespace `
    -n RootManageSharedAccessKey --query primaryConnectionString -o tsv

[Environment]::SetEnvironmentVariable("Messaging__AzureServiceBus__ConnectionString", $cs, "User")
Write-Host "Namespace $Namespace ready. Connection string saved to the user environment variable"
Write-Host "Messaging__AzureServiceBus__ConnectionString (restart Visual Studio / terminals to pick it up)."
Write-Host "Then set Messaging:Provider to 'AzureServiceBus' in tradeflow.shared.json."
Write-Host "Delete when finished: az group delete -n $ResourceGroup --yes"
