// SPDX-License-Identifier: MPL-2.0
namespace CliWatch.Contracts;

public static class MailboxRoles
{
    const string GuardSuffix = "-execution-guard";

    // Registered notification-only harness identifiers, not CLI assignments.
    // Local exit notices use local-guard or cursor-exit-guard; other
    // CLI-specific notices use the registered suffix.
    // Do not infer the role
    // from a task title, a model name, or whether a process is currently absent.
    public static bool IsNotificationOnly(string? harness)
    {
        if (string.IsNullOrWhiteSpace(harness)) return false;
        var name = harness.Trim();
        return name.Equals("local-guard", StringComparison.OrdinalIgnoreCase)
            || name.Equals("cursor-exit-guard", StringComparison.OrdinalIgnoreCase)
            || name.Length > GuardSuffix.Length
                && name.EndsWith(GuardSuffix, StringComparison.OrdinalIgnoreCase);
    }
}
