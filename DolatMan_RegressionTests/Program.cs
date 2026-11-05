using System.Reflection;
using DolatMan.Models;
using DolatMan.Services;
using Microsoft.Playwright;

var scenarios = new[]
{
    new Scenario(
        "tab with inactive CSS class",
        """
        <!-- The shared tab container can have an active class because ایرانی is selected.
             Its class must never be used as the selected state for the اتباع child tab. -->
        <div class="tabs tabs-active">
          <button id="foreigners" class="tab tab-inactive" aria-selected="false">اتباع</button>
          <button id="iranian" class="tab tab-active" aria-selected="true">ایرانی</button>
        </div>
        <script>
          document.getElementById('foreigners').addEventListener('click', () => {
            const foreign = document.getElementById('foreigners');
            const iranian = document.getElementById('iranian');
            foreign.className = 'tab tab-active';
            foreign.setAttribute('aria-selected', 'true');
            iranian.className = 'tab tab-inactive';
            iranian.setAttribute('aria-selected', 'false');
          });
        </script>
        """,
        "() => document.querySelector('#foreigners').classList.contains('tab-active') && document.querySelector('#foreigners').getAttribute('aria-selected') === 'true'"),
    new Scenario(
        "native select option",
        """
        <label for="kind">نوع کاربر</label>
        <select id="kind">
          <option value="iranian" selected>ایرانی</option>
          <option value="foreigner">اتباع</option>
        </select>
        """,
        "() => document.querySelector('#kind').selectedOptions[0].textContent.trim() === 'اتباع'"),
    new Scenario(
        "radio with associated label",
        """
        <div>
          <input type="radio" name="kind" id="foreign" value="foreigner">
          <label for="foreign">اتباع</label>
          <input type="radio" name="kind" id="iranian" value="iranian" checked>
          <label for="iranian">ایرانی</label>
        </div>
        """,
        "() => document.querySelector('#foreign').checked === true")
};

foreach (var scenario in scenarios)
{
    await RunScenarioAsync(scenario);
    Console.WriteLine($"PASS: {scenario.Name}");
}

Console.WriteLine($"All {scenarios.Length} foreigners-selection regression tests passed.");

static async Task RunScenarioAsync(Scenario scenario)
{
    var html = $$"""
        <!doctype html>
        <html lang="fa" dir="rtl">
        <head><meta charset="utf-8"><title>Regression fixture</title></head>
        <body>
          {{scenario.SelectionMarkup}}
          <section id="identity-form">
            <label for="unique">شناسه یکتا</label>
            <input id="unique" name="uniqueId" type="text">
            <label for="fida">کد فیدا</label>
            <input id="fida" name="fidaCode" type="text">
          </section>
        </body>
        </html>
        """;

    using var playwright = await Playwright.CreateAsync();
    await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
    {
        Headless = true
    });
    var context = await browser.NewContextAsync();
    ChromeAutomationService? service = null;

    try
    {
        await context.RouteAsync("**/*", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "text/html; charset=utf-8",
            Body = html
        }));

        var page = await context.NewPageAsync();
        await page.GotoAsync(ChromeAutomationService.RegisterUrl);

        service = new ChromeAutomationService();
        SetPrivateField(service, "_context", context);
        SetPrivateField(service, "_page", page);

        var method = typeof(ChromeAutomationService).GetMethod(
            "OpenRegistrationForQueueAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("OpenRegistrationForQueueAsync was not found.");

        var task = method.Invoke(service, new object[] { CancellationToken.None }) as Task
            ?? throw new InvalidOperationException("Registration method did not return a Task.");
        await task;

        var selected = await page.EvaluateAsync<bool>(scenario.AssertSelectionExpression);
        if (!selected)
            throw new InvalidOperationException($"The foreigners option was not selected in scenario: {scenario.Name}");

        // The real form fields are deliberately present before the tab click. This reproduces
        // the 011 screenshot's failure mode and proves field presence cannot bypass selection.
        var applicant = new Applicant
        {
            FullName = "Regression Test",
            UniqueId = "001234567890",
            FidaCode = "F-00045678",
            BirthDate = ""
        };

        await service.FillIdentityFormAsync(applicant, 0);
        var uniqueValue = await page.Locator("#unique").InputValueAsync();
        var fidaValue = await page.Locator("#fida").InputValueAsync();

        if (uniqueValue != applicant.UniqueId || fidaValue != applicant.FidaCode)
            throw new InvalidOperationException(
                $"Identity fields did not match in {scenario.Name}. Unique='{uniqueValue}', FIDA='{fidaValue}'.");
    }
    finally
    {
        if (service is not null)
            await service.DisposeAsync();
        else
            await context.CloseAsync();
    }
}

static void SetPrivateField(object instance, string name, object value)
{
    var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"Field {name} was not found.");
    field.SetValue(instance, value);
}

sealed record Scenario(string Name, string SelectionMarkup, string AssertSelectionExpression);
