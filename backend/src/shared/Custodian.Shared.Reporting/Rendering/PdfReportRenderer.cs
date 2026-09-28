using System.Globalization;
using Custodian.Shared.Reporting.Models;
using QuestPDF;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Custodian.Shared.Reporting.Rendering;

/// <summary>
/// CSTD-36-2: the one Custodian report layout, drawn with QuestPDF.
/// - Header: "Custodian", report title, tenant, "Generated {UTC} by {actor}", data source.
/// - Filters block, then each section by type. Table headers repeat on every page.
/// - Footer: "Generated from live data at time of request", report code, "Page X of Y".
///
/// Fonts: only QuestPDF's bundled Lato is used (system fonts are off), so the output is the same on a
/// developer machine, in the Docker image and on App Service Linux, which have no fonts installed.
/// Deterministic: no random ids, and the PDF creation date is the model's GeneratedAtUtc.
/// </summary>
public sealed class PdfReportRenderer : IReportRenderer
{
    private const string FontFamily = "Lato";
    private const string AccentColor = "#4338CA"; // indigo-700, as in the app
    private const string MutedColor = "#64748B"; // slate-500
    private const string RuleColor = "#E2E8F0"; // slate-200
    private const string HeaderFill = "#F1F5F9"; // slate-100

    static PdfReportRenderer()
    {
        // Community licence: see docs/reporting.md (PDF library) for the eligibility decision.
        Settings.License = LicenseType.Community;
        Settings.UseSystemFonts = false;
        // Client or engagement names in scripts Lato lacks (e.g. Sinhala, Tamil) would otherwise fail the
        // whole report; they render as replacement glyphs instead.
        Settings.ThrowOnMissingTextGlyphs = false;
    }

    public byte[] RenderPdf(ReportModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var meta = model.Metadata;

        return Document
            .Create(document => document.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(36);
                page.DefaultTextStyle(style => style.FontFamily(FontFamily).FontSize(9).FontColor(Colors.Grey.Darken4));

                page.Header().Element(c => ComposeHeader(c, meta));
                page.Content().PaddingVertical(12).Element(c => ComposeContent(c, model));
                page.Footer().Element(c => ComposeFooter(c, meta));
            }))
            .WithMetadata(new DocumentMetadata
            {
                Title = meta.Title,
                Author = "Custodian",
                Subject = meta.ReportCode,
                Creator = "Custodian",
                Producer = "Custodian",
                CreationDate = meta.GeneratedAtUtc,
                ModifiedDate = meta.GeneratedAtUtc
            })
            .GeneratePdf();
    }

    private static void ComposeHeader(IContainer container, ReportMetadata meta)
    {
        container.BorderBottom(1).BorderColor(RuleColor).PaddingBottom(8).Column(column =>
        {
            column.Item().Text("Custodian").FontSize(9).Bold().FontColor(AccentColor);
            column.Item().Text(meta.Title).FontSize(16).Bold();

            var tenant = meta.TenantDisplayName is null
                ? $"Tenant {meta.TenantId}"
                : $"{meta.TenantDisplayName} ({meta.TenantId})";
            column.Item().Text(tenant).FontColor(MutedColor);
            column.Item().Text(
                    $"Generated {meta.GeneratedAtUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC by {meta.GeneratedBy}")
                .FontColor(MutedColor);
            column.Item().Text($"Source: {meta.DataSource}").FontColor(MutedColor);
        });
    }

    private static void ComposeContent(IContainer container, ReportModel model)
    {
        container.Column(column =>
        {
            column.Spacing(14);
            column.Item().Element(c => ComposeFilters(c, model.Metadata));

            foreach (var section in model.Sections)
            {
                column.Item().Element(c => ComposeSection(c, section));
            }
        });
    }

    private static void ComposeFilters(IContainer container, ReportMetadata meta)
    {
        container.Background(HeaderFill).Padding(8).Column(column =>
        {
            column.Item().Text("Filters").Bold();
            if (meta.AppliedFilters.Count == 0)
            {
                column.Item().Text("No filters applied").FontColor(MutedColor);
                return;
            }

            column.Item().Text(text =>
            {
                var first = true;
                foreach (var (name, value) in meta.AppliedFilters)
                {
                    if (!first) text.Span("   ·   ").FontColor(MutedColor);
                    text.Span($"{name}: ").SemiBold();
                    text.Span(string.IsNullOrEmpty(value) ? "—" : value);
                    first = false;
                }
            });
        });
    }

    private static void ComposeSection(IContainer container, ReportSection section)
    {
        container.Column(column =>
        {
            column.Spacing(4);
            column.Item().Text(section.Title).FontSize(11).Bold().FontColor(AccentColor);

            switch (section)
            {
                case KeyValueSection kv:
                    column.Item().Element(c => ComposeKeyValues(c, kv));
                    break;
                case TableSection table:
                    column.Item().Element(c => ComposeTable(c, table));
                    if (table.Footnote is not null)
                    {
                        column.Item().Text(table.Footnote).Italic().FontSize(8).FontColor(MutedColor);
                    }
                    break;
                case TextSection text:
                    foreach (var paragraph in text.Paragraphs)
                    {
                        column.Item().Text(paragraph);
                    }
                    break;
                case EmptySection empty:
                    column.Item().Text(empty.Message).Italic().FontColor(MutedColor);
                    break;
                default:
                    throw new NotSupportedException($"Report section type '{section.GetType().Name}' has no PDF layout.");
            }
        });
    }

    private static void ComposeKeyValues(IContainer container, KeyValueSection section)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(2);
                columns.RelativeColumn(1);
            });

            foreach (var pair in section.Pairs)
            {
                table.Cell().Element(BodyCell).Text(pair.Label);
                // Values line up on the right, numbers or not ("82.5%", "n/a").
                table.Cell().Element(BodyCell).AlignRight().Text(ReportValue.Format(pair.Value)).SemiBold();
            }
        });
    }

    private static void ComposeTable(IContainer container, TableSection section)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                foreach (var _ in section.Columns)
                {
                    columns.RelativeColumn();
                }
            });

            // QuestPDF repeats the header on every page the table spans.
            table.Header(header =>
            {
                foreach (var column in section.Columns)
                {
                    var cell = header.Cell().Background(HeaderFill).Element(BodyCell);
                    Align(cell, column).Text(column.Header).SemiBold();
                }
            });

            foreach (var row in section.Rows)
            {
                for (var i = 0; i < section.Columns.Count; i++)
                {
                    var cell = table.Cell().Element(BodyCell);
                    Align(cell, section.Columns[i]).Text(ReportValue.Format(row[i]));
                }
            }
        });
    }

    private static IContainer BodyCell(IContainer container) =>
        container.BorderBottom(0.5f).BorderColor(RuleColor).PaddingVertical(3).PaddingHorizontal(4);

    private static IContainer Align(IContainer container, ReportColumn column) =>
        column.Alignment == ReportColumnAlignment.Right ? container.AlignRight() : container.AlignLeft();

    private static void ComposeFooter(IContainer container, ReportMetadata meta)
    {
        container.BorderTop(1).BorderColor(RuleColor).PaddingTop(6).Row(row =>
        {
            row.RelativeItem()
                .Text($"Generated from live data at time of request · {meta.ReportCode}")
                .FontSize(8).FontColor(MutedColor);
            row.ConstantItem(90).AlignRight().Text(text =>
            {
                text.DefaultTextStyle(style => style.FontSize(8).FontColor(MutedColor));
                text.Span("Page ");
                text.CurrentPageNumber();
                text.Span(" of ");
                text.TotalPages();
            });
        });
    }
}
