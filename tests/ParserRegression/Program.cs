using EbayKleinanzeigenCrawler.ErrorHandling;
using EbayKleinanzeigenCrawler.Interfaces;
using EbayKleinanzeigenCrawler.Parser.Implementations;
using HtmlAgilityPack;
using Serilog;

var parser = new InspectableParser();
int checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    checks++;
    Console.WriteLine("PASS " + description);
}
HtmlDocument Html(string source) { var d = new HtmlDocument(); d.LoadHtml(source); return d; }
var modern = Html("""
<ul id="srchrslt-adtable">
<li><article class="flex p-medium" data-adid="123" data-href="/s-anzeige/example/123-216-1">
<a href="/s-anzeige/example/123-216-1">image</a><a href="/s-anzeige/example/123-216-1">title</a>
<div><svg data-title="calendarOutline"></svg><span>Heute, 07:34</span></div>
<p class="text-bodyRegular">Description mentions 500 €.</p>
<p class="text-title3 font-strong text-secondary">250&nbsp;€ VB</p>
<p class="text-title3 line-through">300 €</p></article></li>
<li><article class="advertisement"><a href="https://example.org">advertisement</a></article></li>
</ul>
""");
var modernResult = parser.ParseLinks(modern).Single();
Check(modernResult.Link.ToString() == "https://www.kleinanzeigen.de/s-anzeige/example/123-216-1", "Astro card yields one canonical ad link despite repeated anchors");
Check(modernResult.CreationDate == "Heute, 07:34", "Astro date is associated with the calendar icon");
Check(modernResult.Price == "250\u00a0€ VB", "Astro price excludes description and crossed-out old price");
var legacy = Html("""
<ul id="srchrslt-adtable"><li><article class="aditem extra">
<div class="aditem-image extra"><a href="/s-anzeige/legacy/456-1-1">image</a></div>
<div><div class="aditem-main--top--right extra">Gestern</div>
<p class="aditem-main--middle--price-shipping--price extra">Zu verschenken</p></div>
</article></li></ul>
""");
var legacyResult = parser.ParseLinks(legacy).Single();
Check(legacyResult.Link.AbsolutePath == "/s-anzeige/legacy/456-1-1" && legacyResult.Price == "Zu verschenken" && legacyResult.CreationDate == "Gestern", "Legacy markup and optional CSS classes still parse");
var paging = Html("""
<a class="pagination-page extra" href="/s-autos/seite:2/k0c216">2</a>
<a aria-label="Nächste" href="https://www.kleinanzeigen.de/s-autos/seite:2/k0c216">Next</a>
<a href="/s-autos/seite:3/k0c216">3</a>
<a href="https://example.org/seite:4">Other site</a>
<a class="pagination-page" href="javascript:alert(1)">Invalid</a>
""");
Check(parser.GetAdditionalPages(paging).Count == 2, "Pagination handles both layouts, deduplicates and rejects other origins");
Check(!parser.ParseLinks(Html("<ul id='srchrslt-adtable'></ul>")).Any(), "Genuinely empty search remains empty");
Check(parser.Link(Html("<article data-href='https://example.org/s-anzeige/123'></article>").DocumentNode.FirstChild) is null, "Ad links cannot escape the Kleinanzeigen origin");
var noMetadata = parser.ParseLinks(Html("<ul id='srchrslt-adtable'><article data-adid='789' data-href='/s-anzeige/optional/789-1-1'></article></ul>")).Single();
Check(noMetadata.Price == "" && noMetadata.CreationDate == "", "Ads without price or date are still returned");
if (args.Length > 0)
{
    var live = new HtmlDocument(); live.Load(args[0]);
    var results = parser.ParseLinks(live).ToList();
    Check(results.Count > 0, "Live search HTML contains parsed results");
    Check(results.All(r => r.Link.Host == "www.kleinanzeigen.de"), "Live ad URLs stay on the expected origin");
    Console.WriteLine($"LIVE: {results.Count} ads, {parser.GetAdditionalPages(live).Count} additional pages");
}
Console.WriteLine($"{checks} regression checks passed.");

sealed class InspectableParser : EbayKleinanzeigenParser
{
    public InspectableParser() : base(new LoggerConfiguration().CreateLogger(), new NoQuery(), new NoErrors()) { }
    public Uri Link(HtmlNode node) => ParseResultLink(node);
}
sealed class NoQuery : IQueryExecutor
{
    public void Initialize(TimeSpan interval, uint count, string invalidHtml) { }
    public Task<HtmlDocument> GetHtml(Uri uri) => throw new NotSupportedException();
    public bool WaitUntilQueriesArePossibleAgain() => true;
}
sealed class NoErrors : IErrorStatistics
{
    public void AmendErrorStatistic(ErrorType type) => throw new Exception("Unexpected parser error: " + type);
    public void NotifyOnThreshold() { }
}
