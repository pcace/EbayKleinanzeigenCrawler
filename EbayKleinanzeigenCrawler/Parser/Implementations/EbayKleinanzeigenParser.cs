using HtmlAgilityPack;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using EbayKleinanzeigenCrawler.Interfaces;

namespace EbayKleinanzeigenCrawler.Parser.Implementations;

public class EbayKleinanzeigenParser : ParserBase
{
    private const string BaseUrl = "https://www.kleinanzeigen.de";
    
    /// <summary>
    /// This interval is used by EbayKleinanzeigen. They only allow 40 queries every 5 minutes. Above that, they obfuscate their HTML.
    /// To make sure not to exceed this limit, it is hard-coded here.
    /// </summary>
    protected override TimeSpan TimeToWaitBetweenMaxAmountOfRequests => TimeSpan.FromMinutes(5);
    protected override uint AllowedRequestsPerTimespan => 40;
    protected override string InvalidHtml => "<html><head><meta charset=\"utf-8\"><script>";

    public EbayKleinanzeigenParser(ILogger logger, IQueryExecutor queryExecutor, IErrorStatistics errorStatistics) : base(logger, queryExecutor, errorStatistics) { }

    protected override bool EnsureValidHtml(HtmlDocument resultPage)
    {
        // TODO: An URL with query params seems to forget the query keywords and searches for anything. Do not allow ? in URLs.
        // e.g. /s-anzeigen/96123/anzeige:angebote/lescha-betonmischer/c0-l6895?distance=50&maxPrice=100
        if (resultPage.ParsedText.StartsWith(InvalidHtml) || resultPage.ParsedText.Contains("429 Too many requests from"))
        {
            Logger.Warning("Invalid HTML detected");
            return false;
        }

        if (resultPage.ParsedText.Contains("Die gewünschte Anzeige ist nicht mehr verfügbar"))
        {
            Logger.Warning("Tried to parse ad which does not exist anymore");
            return false;
        }

        return true;
    }

    protected override bool ShouldSkipResult(HtmlNode result)
    {
        var isProShopLink =
            result
                .Descendants("div")
                .Any(d => d.Attributes.Any(a => a.Value == "badge-hint-pro-small-srp"))
            || result
                .Descendants("i")
                .Any(d => d.Attributes.Any(a => a.Value.Contains("icon-feature-topad")));

        if (isProShopLink)
        {
            // Filter out Pro-Shop links
            Logger.Verbose("Skipping Pro-Shop link");
            return true;
        }

        return false;
    }

    public override List<Uri> GetAdditionalPages(HtmlDocument document)
    {
        return document.DocumentNode.Descendants("a")
            .Where(a => HasClass(a, "pagination-page") ||
                        a.GetAttributeValue("href", "").Contains("/seite:"))
            .Select(a => ResolveLink(a.GetAttributeValue("href", "")))
            .Where(uri => uri is not null)
            .Distinct()
            .ToList();
    }

    protected override List<HtmlNode> ParseResults(HtmlDocument resultPage)
    {
        // The Astro search page uses data-adid/data-href instead of aditem.
        // Keep the old markup working for pages that have not migrated yet.
        return resultPage.GetElementbyId("srchrslt-adtable")?
            .Descendants("article")
            .Where(article => article.Attributes["data-adid"] is not null || HasClass(article, "aditem"))
            .ToList() ?? new List<HtmlNode>();
    }

    protected override Uri ParseResultLink(HtmlNode result)
    {
        var cardLink = ResolveLink(result.GetAttributeValue("data-href", ""));
        if (cardLink?.AbsolutePath.StartsWith("/s-anzeige/") == true)
        {
            return cardLink;
        }

        return result.Descendants("a")
            .Select(a => ResolveLink(a.GetAttributeValue("href", "")))
            .FirstOrDefault(uri => uri?.AbsolutePath.StartsWith("/s-anzeige/") == true);
    }

    protected override string ParseResultDate(HtmlNode result)
    {
        var legacy = result.Descendants("div").FirstOrDefault(n => HasClass(n, "aditem-main--top--right"));
        var calendar = result.Descendants("svg")
            .FirstOrDefault(n => n.GetAttributeValue("data-title", "") == "calendarOutline");
        var current = calendar?.ParentNode.ChildNodes.FirstOrDefault(n => n.Name == "span");
        return Text(legacy ?? current);
    }

    protected override string ParseResultPrice(HtmlNode result)
    {
        var price = result.Descendants("p").FirstOrDefault(n =>
            HasClass(n, "aditem-main--middle--price-shipping--price") ||
            (HasClass(n, "text-title3") && HasClass(n, "font-strong") && !HasClass(n, "line-through")));
        return Text(price);
    }

    private static bool HasClass(HtmlNode node, string value) =>
        node.GetAttributeValue("class", "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Contains(value);

    private static string Text(HtmlNode node) => node is null ? null : HtmlEntity.DeEntitize(node.InnerText).Trim();

    private static Uri ResolveLink(string href)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        return Uri.TryCreate(new Uri(BaseUrl), HtmlEntity.DeEntitize(href), out var uri) &&
               uri.Scheme == Uri.UriSchemeHttps && uri.Host == new Uri(BaseUrl).Host ? uri : null;
    }

    protected override string ParseTitle(HtmlDocument document)
    {
        if (document.ParsedText.Contains("Die gewünschte Anzeige ist nicht mehr verfügbar"))
        {
            Logger.Warning("Tried to parse ad which does not exist anymore");
            return null;
        }

        return document.DocumentNode
            .Descendants("h1")
            .SingleOrDefault(div => div.GetAttributeValue("id", "").Contains("viewad-title"))?
            .InnerHtml;
    }

    protected override string ParseDescriptionText(HtmlDocument document)
    {
        return document.DocumentNode
            .Descendants("p")
            .SingleOrDefault(div => div.GetAttributeValue("id", "").Contains("viewad-description-text"))?
            .InnerHtml;
    }
}
