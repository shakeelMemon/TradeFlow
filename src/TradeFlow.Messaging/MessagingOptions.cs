namespace TradeFlow.Messaging;

public sealed class MessagingOptions
{
    /// <summary>"LocalFile" (no cloud needed), "AzureServiceBus", "Aws" or "InMemory" (tests).</summary>
    public string Provider { get; set; } = "LocalFile";

    /// <summary>
    /// This program's own subscription on the topic (set per program in its appsettings.json):
    /// "simcorp" for SimCorp, "fix-gateway" for the FIX gateway. Every subscription gets its own copy of each event.
    /// </summary>
    public string Subscription { get; set; } = "";

    public LocalFileOptions LocalFile { get; set; } = new();
    public AzureServiceBusOptions AzureServiceBus { get; set; } = new();
    public AwsOptions Aws { get; set; } = new();
}

public sealed class LocalFileOptions
{
    public string Root { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TradeFlow", "bus");

    public string Topic { get; set; } = "trade-orders";

    /// <summary>Subscriptions that receive a copy of every published event (comma separated).</summary>
    public string Subscriptions { get; set; } = "simcorp,fix-gateway";
}

public sealed class AzureServiceBusOptions
{
    /// <summary>
    /// Preferred: the namespace host, e.g. "mybus.servicebus.windows.net". The app signs in with an
    /// Entra ID identity (your az login locally, a Managed Identity in Azure). No secrets anywhere.
    /// </summary>
    public string FullyQualifiedNamespace { get; set; } = "";

    /// <summary>Fallback when the namespace allows SAS keys. Keep it in an environment variable, never in a file.</summary>
    public string ConnectionString { get; set; } = "";
    public string Topic { get; set; } = "trade-orders";
    public string Subscription { get; set; } = "simcorp";
}

public sealed class AwsOptions
{
    public string Region { get; set; } = "ca-central-1";
    public string TopicArn { get; set; } = "";
    public string QueueUrl { get; set; } = "";
}
