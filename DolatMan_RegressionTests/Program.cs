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
        "() => document.querySelector('#foreign').checked === true"),
    new Scenario(
        "segmented DIV tab without button or ARIA roles",
        """
        <div id="segmented-control" class="segmented-control">
          <div id="foreign-tab" class="segment-item">
            <span class="globe-icon">◉</span><span>اتباع</span>
          </div>
          <div id="iranian-tab" class="segment-item active">
            <span class="flag-icon">⚑</span><span>ایرانی</span>
          </div>
        </div>
        <script>
          document.getElementById('segmented-control').addEventListener('click', event => {
            const item = event.target.closest('.segment-item');
            if (!item) return;
            document.querySelectorAll('.segment-item').forEach(x => x.classList.remove('active'));
            item.classList.add('active');
          });
        </script>
        """,
        "() => document.querySelector('#foreign-tab').classList.contains('active')")
};

foreach (var scenario in scenarios)
{
    await RunScenarioAsync(scenario);
    Console.WriteLine($"PASS: {scenario.Name}");
}



await RunClickableLookupRegressionAsync();
Console.WriteLine("PASS: stable clickable lookup during DOM re-render.");

await RunOtpAutoVerifyRegressionAsync(singleInput: true);
Console.WriteLine("PASS: single OTP input auto-verifies without a confirm button.");

await RunOtpAutoVerifyRegressionAsync(singleInput: false);
Console.WriteLine("PASS: split OTP boxes auto-verify on the final digit without a confirm button.");

await RunOtpNavigationRegressionAsync();
Console.WriteLine("PASS: OTP entry stops touching digit locators after automatic navigation.");

await RunIdentityFieldsAndCustomDateRegressionAsync();
Console.WriteLine("PASS: sibling FIDA label and custom Jalali day/month/year dropdowns.");

Console.WriteLine($"All {scenarios.Length} foreigners-selection regression tests passed.");

static async Task RunClickableLookupRegressionAsync()
{
    using var playwright = await Playwright.CreateAsync();
    await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
    {
        Headless = true
    });
    var context = await browser.NewContextAsync();
    var page = await context.NewPageAsync();
    await page.SetContentAsync("""
        <!doctype html>
        <html lang="fa">
        <body>
          <button id="other">گزینه دیگر</button>
          <button id="send">ارسال کد</button>
          <output id="result"></output>
          <script>
            document.getElementById('send').addEventListener('click', () => {
              document.getElementById('result').textContent = 'clicked';
            });
            // Simulate the portal replacing an unrelated button while the helper scans.
            setTimeout(() => {
              const old = document.getElementById('other');
              if (old) old.replaceWith(Object.assign(document.createElement('button'), {
                id: 'other-new', textContent: 'گزینه جایگزین'
              }));
            }, 0);
          </script>
        </body>
        </html>
        """);

    var service = new ChromeAutomationService();
    SetPrivateField(service, "_context", context);
    SetPrivateField(service, "_page", page);
    try
    {
        var method = typeof(ChromeAutomationService).GetMethod(
            "FindClickableByTextAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("FindClickableByTextAsync was not found.");
        var task = method.Invoke(service, new object[] { new[] { "ارسال کد" } }) as Task<ILocator?>
            ?? throw new InvalidOperationException("Clickable lookup did not return a Task.");
        var button = await task
            ?? throw new InvalidOperationException("The matching button was not found.");
        await button.ClickAsync();
        var clicked = await page.Locator("#result").InnerTextAsync();
        if (clicked != "clicked")
            throw new InvalidOperationException("The returned locator did not click the expected button.");
    }
    finally
    {
        await service.DisposeAsync();
    }
}


static async Task RunOtpAutoVerifyRegressionAsync(bool singleInput)
{
    var html = singleInput
        ? """
          <!doctype html><html lang="fa" dir="rtl"><body>
            <label for="otp">کد پیامکی</label>
            <input id="otp" name="otp" type="text" autocomplete="one-time-code"
                   placeholder="کد پیامکی" maxlength="6">
            <output id="status"></output>
            <script>
              const otp = document.getElementById('otp');
              otp.addEventListener('input', () => {
                if (otp.value.length === 6)
                  document.body.dataset.autoVerified = 'true';
              });
            </script>
          </body></html>
          """
        : """
          <!doctype html><html lang="fa" dir="rtl"><body>
            <section id="otp-boxes">
              <input class="otp-digit" maxlength="1" inputmode="numeric">
              <input class="otp-digit" maxlength="1" inputmode="numeric">
              <input class="otp-digit" maxlength="1" inputmode="numeric">
              <input class="otp-digit" maxlength="1" inputmode="numeric">
              <input class="otp-digit" maxlength="1" inputmode="numeric">
              <input class="otp-digit" maxlength="1" inputmode="numeric">
            </section>
            <output id="status"></output>
            <script>
              const boxes = [...document.querySelectorAll('.otp-digit')];
              boxes.forEach(box => box.addEventListener('input', () => {
                if (boxes.map(x => x.value).join('').length === 6)
                  document.body.dataset.autoVerified = 'true';
              }));
            </script>
          </body></html>
          """;

    using var playwright = await Playwright.CreateAsync();
    await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
    {
        Headless = true
    });
    var context = await browser.NewContextAsync();
    await context.RouteAsync("**/*", route => route.FulfillAsync(new RouteFulfillOptions
    {
        Status = 200,
        ContentType = "text/html; charset=utf-8",
        Body = html
    }));
    var page = await context.NewPageAsync();
    await page.GotoAsync(ChromeAutomationService.RegisterUrl);

    var service = new ChromeAutomationService();
    SetPrivateField(service, "_context", context);
    SetPrivateField(service, "_page", page);

    try
    {
        var method = typeof(ChromeAutomationService).GetMethod(
            "EnterOtpForAutomaticVerificationAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Automatic OTP entry method was not found.");
        var task = method.Invoke(service, new object[] { "123456" }) as Task
            ?? throw new InvalidOperationException("Automatic OTP method did not return a Task.");
        await task;

        var autoVerified = await page.EvaluateAsync<bool>(
            "() => document.body.dataset.autoVerified === 'true'");
        if (!autoVerified)
            throw new InvalidOperationException("The last OTP digit did not trigger automatic verification.");

        var buttons = await page.Locator("button,input[type=submit]").CountAsync();
        if (buttons != 0)
            throw new InvalidOperationException("The test fixture must not contain a confirm button.");

        string entered;
        if (singleInput)
        {
            entered = await page.Locator("#otp").InputValueAsync();
        }
        else
        {
            var boxes = page.Locator(".otp-digit");
            var count = await boxes.CountAsync();
            var digits = new System.Text.StringBuilder();
            for (var i = 0; i < count; i++)
                digits.Append(await boxes.Nth(i).InputValueAsync());
            entered = digits.ToString();
        }

        if (entered != "123456")
            throw new InvalidOperationException($"OTP value mismatch: '{entered}'.");
    }
    finally
    {
        await service.DisposeAsync();
    }
}

static async Task RunOtpNavigationRegressionAsync()
{
    const string html = """
        <!doctype html><html lang="fa" dir="rtl"><body>
          <section id="otp-boxes">
            <input class="otp-digit" maxlength="1" inputmode="numeric">
            <input class="otp-digit" maxlength="1" inputmode="numeric">
            <input class="otp-digit" maxlength="1" inputmode="numeric">
            <input class="otp-digit" maxlength="1" inputmode="numeric">
            <input class="otp-digit" maxlength="1" inputmode="numeric">
            <input class="otp-digit" maxlength="1" inputmode="numeric">
          </section>
          <script>
            const boxes = [...document.querySelectorAll('.otp-digit')];
            boxes.forEach(box => box.addEventListener('input', () => {
              if (boxes.map(x => x.value).join('').length === 6) {
                document.querySelector('#otp-boxes').remove();
                history.pushState({}, '', '/register');
              }
            }));
          </script>
        </body></html>
        """;

    using var playwright = await Playwright.CreateAsync();
    await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
    {
        Headless = true
    });
    var context = await browser.NewContextAsync();
    await context.RouteAsync("https://sso.my.gov.ir/login", route => route.FulfillAsync(new RouteFulfillOptions
    {
        Status = 200,
        ContentType = "text/html; charset=utf-8",
        Body = html
    }));
    var page = await context.NewPageAsync();
    await page.GotoAsync("https://sso.my.gov.ir/login");

    var service = new ChromeAutomationService();
    SetPrivateField(service, "_context", context);
    SetPrivateField(service, "_page", page);

    try
    {
        var method = typeof(ChromeAutomationService).GetMethod(
            "EnterOtpForAutomaticVerificationAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Automatic OTP entry method was not found.");
        var task = method.Invoke(service, new object[] { "123456" }) as Task
            ?? throw new InvalidOperationException("Automatic OTP method did not return a Task.");
        await task;

        if (!page.Url.EndsWith("/register", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Expected automatic navigation to /register, got '{page.Url}'.");
        if (await page.Locator("input[maxlength='1']").CountAsync() != 0)
            throw new InvalidOperationException("OTP fields should have been removed after navigation.");
    }
    finally
    {
        await service.DisposeAsync();
    }
}

static async Task RunIdentityFieldsAndCustomDateRegressionAsync()
{
    const string html = """
        <!doctype html>
        <html lang="fa" dir="rtl">
        <body>
          <div id="identity-kind">
            <button id="foreign" class="active" aria-selected="true">اتباع</button>
            <button id="iranian" aria-selected="false">ایرانی</button>
          </div>
          <section id="identity-form">
            <div class="field">
              <input id="unique" type="text" placeholder="شماره گذرنامه/فراگیر/شناسه یکتا">
            </div>
            <div class="field">
              <span>کد فیدا</span>
              <input id="fida" type="text" autocomplete="off">
            </div>
          </section>
          <section id="birth-date">
            <div id="day" class="custom-dropdown"><span>روز</span></div>
            <div id="month" class="custom-dropdown"><span>ماه</span></div>
            <div id="year" class="custom-dropdown"><span>سال تولد</span></div>
          </section>
          <script>
            const monthNames = ['فروردین','اردیبهشت','خرداد','تیر','مرداد','شهریور',
                                'مهر','آبان','آذر','دی','بهمن','اسفند'];
            const values = {
              day: Array.from({length:31}, (_,i) => String(i+1)),
              month: monthNames,
              year: ['1370','1371','1372']
            };
            function openOptions(name) {
              document.querySelector('#date-options')?.remove();
              const list = document.createElement('ul');
              list.id = 'date-options';
              for (const label of values[name]) {
                const option = document.createElement('li');
                option.setAttribute('role','option');
                option.textContent = label;
                option.addEventListener('click', () => {
                  document.querySelector('#'+name+' span').textContent = label;
                  list.remove();
                });
                list.appendChild(option);
              }
              document.body.appendChild(list);
            }
            document.querySelectorAll('.custom-dropdown').forEach(el =>
              el.addEventListener('click', () => openOptions(el.id)));
          </script>
        </body>
        </html>
        """;

    using var playwright = await Playwright.CreateAsync();
    await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
    var context = await browser.NewContextAsync();
    var page = await context.NewPageAsync();
    await page.SetContentAsync(html);

    var service = new ChromeAutomationService();
    SetPrivateField(service, "_context", context);
    SetPrivateField(service, "_page", page);

    try
    {
        var applicant = new Applicant
        {
            FullName = "Regression Test",
            UniqueId = "001234567890",
            FidaCode = "F-00045678",
            BirthDate = "15/02/1370",
            MappingIndex = 0
        };

        await service.FillIdentityFormAsync(applicant, 0);

        var uniqueValue = await page.Locator("#unique").InputValueAsync();
        var fidaValue = await page.Locator("#fida").InputValueAsync();
        if (uniqueValue != applicant.UniqueId || fidaValue != applicant.FidaCode)
            throw new InvalidOperationException(
                $"Identity fields mismatch. Unique='{uniqueValue}', FIDA='{fidaValue}'.");

        var selectedDay = (await page.Locator("#day").InnerTextAsync()).Trim();
        var selectedMonth = (await page.Locator("#month").InnerTextAsync()).Trim();
        var selectedYear = (await page.Locator("#year").InnerTextAsync()).Trim();

        if (selectedDay != "15" || selectedMonth != "اردیبهشت" || selectedYear != "1370")
            throw new InvalidOperationException(
                $"Jalali date mismatch. Got {selectedDay}/{selectedMonth}/{selectedYear}.");
    }
    finally
    {
        await service.DisposeAsync();
    }
}

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
