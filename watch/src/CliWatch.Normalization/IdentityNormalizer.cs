// SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Nodes;
using CliWatch.Contracts;

namespace CliWatch.Normalization;

/// <summary>
/// Pass-through: each collected document becomes a fact. No Telephone-line axis invention.
/// </summary>
public sealed class IdentityNormalizer : INormalizer
{
    public FactBatch Normalize(CollectionBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var facts = new List<SourceFact>();
        var issues = new List<SourceIssue>(batch.Issues ?? Array.Empty<SourceIssue>());
        foreach (var doc in batch.Documents ?? Array.Empty<RawDocument>())
        {
            var links = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["location"] = doc.Location
            };
            var key = ObservationIdentity.Text(doc.Data, "job_key") ?? ObservationIdentity.Text(doc.Data, "key");
            if (!string.IsNullOrWhiteSpace(key))
                links["job_key"] = key;
            var mailboxRoot = ObservationIdentity.Text(doc.Data, "mailbox_root");
            if (!string.IsNullOrWhiteSpace(mailboxRoot))
                links["mailbox_root"] = mailboxRoot;
            facts.Add(new SourceFact(
                doc.Kind,
                doc.Id,
                doc.ProjectHint,
                links,
                doc.Data,
                new[]
                {
                    new EvidenceRef(doc.Id, doc.Location, doc.Kind, doc.ObservedAt, doc.FactAt, DataQuality.Fresh)
                }));
        }

        return new FactBatch(batch.CollectedAt, facts, issues);
    }
}
