// SPDX-License-Identifier: MPL-2.0
using System.Security.Cryptography;
using System.Text;

namespace CliWatch.App;

public sealed class SendEvaluation
{
    public bool Allowed { get; init; }
    public bool RequiresPreview { get; init; }
    public bool Blocked { get; init; }
    public bool Duplicate { get; init; }
    public string Message { get; init; } = "";
    public string? Fingerprint { get; init; }
    public string? Recipient { get; init; }
    public string? Content { get; init; }

    public static SendEvaluation Block(string message, bool duplicate = false) => new()
    {
        Blocked = true,
        Duplicate = duplicate,
        Message = message
    };

    public static SendEvaluation Preview(string fingerprint, string recipient, string content) => new()
    {
        RequiresPreview = true,
        Fingerprint = fingerprint,
        Recipient = recipient,
        Content = content,
        Message = "发送能力若接入，必须先预览收件人与内容，并防重复。当前版本只支持复制摘要，不外发。"
    };
}

/// <summary>
/// Send is not wired in this package. If a later send capability is attached,
/// preview + anti-duplicate are mandatory. send_messages_automatically stays false.
/// </summary>
public static class HelpSendGuard
{
    public static bool SendCapabilityWired => false;
    public const bool DefaultSendAutomatically = false;

    public static SendEvaluation Evaluate(bool sendAutomatically, string content, string recipient, string? lastFingerprint)
    {
        if (sendAutomatically || !SendCapabilityWired)
        {
            if (sendAutomatically)
            {
                return SendEvaluation.Block("禁止后台自动外发。send_messages_automatically 必须为 false。");
            }
        }

        if (!SendCapabilityWired)
        {
            return SendEvaluation.Block("发送能力未接入。请复制摘要后由 User 显式处理。");
        }

        var fp = Fingerprint(recipient, content);
        if (!string.IsNullOrEmpty(lastFingerprint) && string.Equals(lastFingerprint, fp, StringComparison.Ordinal))
            return SendEvaluation.Block("与上次预览内容相同，已防重复。", duplicate: true);

        return SendEvaluation.Preview(fp, recipient, content);
    }

    public static string Fingerprint(string recipient, string content)
    {
        var raw = (recipient ?? "") + "\n" + (content ?? "");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes);
    }
}
