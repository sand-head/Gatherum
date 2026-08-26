using System.Text;
using Gatherum.Infrastructure.Bookmarks;
using Gatherum.Infrastructure.Epub;
using Microsoft.Playwright;

namespace Gatherum.Tests;

/// <summary>The chapter rendering against a real headless Chromium: the pager is a
/// script, and only a browser can say whether it pages. Machines with no Chromium
/// skip, the same bargain the archiver tests strike.</summary>
public sealed class EpubReaderBrowserTests
{
    [Fact]
    public async Task A_long_chapter_flows_into_pages_and_the_pager_turns_them()
    {
        if (BrowserPageArchiver.ResolveBrowser("") is not { } executable)
            return;

        var paragraphs = string.Concat(Enumerable.Range(1, 200).Select(n =>
            $"<p>Paragraph {n}, in which the closet is warmer than paragraph {n - 1}.</p>"));
        var epub = EpubFixtures.Zip(
            EpubFixtures.Text("META-INF/container.xml", """
                <?xml version="1.0"?>
                <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0">
                  <rootfiles><rootfile full-path="content.opf" media-type="application/oebps-package+xml"/></rootfiles>
                </container>
                """),
            EpubFixtures.Text("content.opf", """
                <?xml version="1.0"?>
                <package xmlns="http://www.idpf.org/2007/opf" xmlns:dc="http://purl.org/dc/elements/1.1/" version="3.0">
                  <metadata><dc:title>Long Book</dc:title></metadata>
                  <manifest><item id="c1" href="ch1.xhtml" media-type="application/xhtml+xml"/></manifest>
                  <spine><itemref idref="c1"/></spine>
                </package>
                """),
            EpubFixtures.Text("ch1.xhtml",
                $"<html><body>{paragraphs}</body></html>"));

        using var book = await EpubBook.OpenAsync(new MemoryStream(epub));
        var html = await EpubChapterHtml.RenderAsync(book, 0);
        var path = Path.Combine(Path.GetTempPath(), $"gatherum-epub-{Guid.NewGuid():N}.html");
        await File.WriteAllTextAsync(path, html, Encoding.UTF8);
        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(
                new() { ExecutablePath = executable });
            var page = await browser.NewPageAsync(
                new() { ViewportSize = new() { Width = 900, Height = 600 } });
            await page.GotoAsync("file://" + path);

            // The chapter flowed into more than one page, and the foot says so.
            await page.WaitForFunctionAsync(
                "() => /^1 \\/ \\d+$/.test(document.getElementById('epub-page').textContent)");
            var label = await page.TextContentAsync("#epub-page");
            Assert.True(int.Parse(label!.Split('/')[1]) > 1);

            // The edge button turns the page forward; the keyboard turns it back.
            await page.ClickAsync("#epub-next");
            await page.WaitForFunctionAsync(
                "() => document.getElementById('epub-page').textContent.startsWith('2 /')");
            await page.Keyboard.PressAsync("ArrowLeft");
            await page.WaitForFunctionAsync(
                "() => document.getElementById('epub-page').textContent.startsWith('1 /')");

            // And the first paragraph is on the first page, not scrolled off somewhere.
            Assert.True(await page.IsVisibleAsync("text=Paragraph 1,"));

            // Every turn reports how far through the chapter the reader is — the
            // message the hosting page turns into the position the server remembers.
            await page.EvaluateAsync(
                "() => { window.__progress = null; addEventListener('message', e => " +
                "{ if (typeof e.data?.gatherumEpubProgress === 'number') " +
                "window.__progress = e.data.gatherumEpubProgress; }); }");
            await page.ClickAsync("#epub-next");
            await page.WaitForFunctionAsync("() => typeof window.__progress === 'number'");

            // A saved fraction arrives as a fragment and reopens the chapter there:
            // #at=1 is the last page. (The reload is what a fresh visit is; a bare
            // hash change would be a same-document navigation the pager never sees.)
            await page.GotoAsync("file://" + path + "#at=1");
            await page.ReloadAsync();
            await page.WaitForFunctionAsync(
                "() => { const t = document.getElementById('epub-page').textContent;" +
                " const [now, all] = t.split(' / '); return now === all && +all > 1; }");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
