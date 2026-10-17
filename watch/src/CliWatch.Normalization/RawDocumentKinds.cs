// SPDX-License-Identifier: MPL-2.0
namespace CliWatch.Normalization;

/// <summary>Contract RawDocument.Kind constants (mirrors Collection FileCollector; read-only reference).</summary>
internal static class RawDocumentKinds
{
    public const string ProjectRegistry = "project_registry";
    public const string ActiveWork = "active_work";
    public const string LeadRun = "lead_run";
    public const string LeadOwner = "lead_owner";
    public const string CliChild = "cli_child";
    public const string CliLifecycle = "cli_lifecycle";
    public const string ProcessObservation = "process_observation";
    public const string LineDispatch = "line_dispatch";
    public const string LineReceipt = "line_receipt";
    public const string LineDelivery = "line_delivery";
    public const string WakeIntent = "wake_intent";
    public const string WakeAttempt = "wake_attempt";
    public const string DirectRequest = "direct_request";
    public const string DirectReceipt = "direct_receipt";
    public const string RouteStatus = "route_status";
    public const string BotRegistry = "bot_registry";
    public const string BotResultPointer = "bot_result_pointer";
    public const string BotAcceptance = "bot_acceptance";
    public const string GenerationStatus = "generation_status";
    public const string CurrentResult = "current_result";

    public static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        ProjectRegistry, ActiveWork, LeadRun, LeadOwner, CliChild, CliLifecycle,
        ProcessObservation, LineDispatch, LineReceipt, LineDelivery, WakeIntent,
        WakeAttempt, DirectRequest, DirectReceipt, RouteStatus, BotRegistry,
        BotResultPointer, BotAcceptance, GenerationStatus, CurrentResult
    };
}
