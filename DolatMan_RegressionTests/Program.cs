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

await RunOtpDoesNotOverwriteNationalIdRegressionAsync();
Console.WriteLine("PASS: OTP code is never typed into the lone national-ID input on /register.");

await RunIdentityFieldsAndCustomDateRegressionAsync();
Console.WriteLine("PASS: sibling FIDA label and custom Jalali day/month/year dropdowns.");

await RunOfficialPortalMarkupRegressionAsync();
Console.WriteLine("PASS: exact sso.my.gov.ir foreigners tab, identity fields and birth-date picker markup.");

await RunAuthenticatedSecondTabRegressionAsync();
Console.WriteLine("PASS: automation adopts the authenticated SSO registration tab opened after OTP.");

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
              const verifyOtp = () => {
                if (boxes.every(x => x.value.length === 1) &&
                    boxes.map(x => x.value).join('').length === 6)
                  document.body.dataset.autoVerified = 'true';
              };
              boxes.forEach(box =>
                ['input','keyup','change'].forEach(eventName =>
                  box.addEventListener(eventName, verifyOtp)));

            </script>
          </body></html>
          """;

    using var playwright = await Playwright.CreateAsync();
    await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
    {
        Headless = true
    });
    var context = await browser.NewContextAsync();
    var page = await context.NewPageAsync();
    await page.SetContentAsync(html);    var service = new ChromeAutomationService();
    SetPrivateField(service, "_context", context);
    SetPrivateField(service, "_page", page);

    try
    {
        var method = typeof(ChromeAutomationService).GetMethod(
            "EnterOtpForAutomaticVerificationAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Automatic OTP entry method was not found.");
        var task = method.Invoke(service, new object[] { "123456", new Progress<string>(_ => { }) }) as Task
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
                document.body.innerHTML =
                  '<h1>درگاه یکپارچه احراز هویت</h1>' +
                  '<div>اتباع</div><div>ایرانی</div>' +
                  '<div>روز ماه سال تولد</div><div>کد فیدا</div>' +
                  '<input placeholder="شماره گذرنامه/شناسه یکتا">';
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
        var task = method.Invoke(service, new object[] { "123456", new Progress<string>(_ => { }) }) as Task
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

static async Task RunOtpDoesNotOverwriteNationalIdRegressionAsync()
{
    const string html = """
        <!doctype html><html lang="fa" dir="rtl"><body>
          <div>درگاه یکپارچه احراز هویت</div>
          <div>اتباع</div><div>ایرانی</div>
          <div>تاریخ تولد</div><div>کد فیدا</div>
          <form id="register-iranian-form">
            <input id="national-id" type="text" placeholder="کد ملی" value="">
            <input id="birth-date-iranian" type="hidden" value="">
          </form>
          <form id="register-foreigner-form" style="display:none">
            <input id="passport-code" type="text" placeholder="شماره گذرنامه/شناسه یکتا">
            <input id="shenasaei-code" type="text" placeholder="کد فیدا">
          </form>
        </body></html>
        """;

    using var playwright = await Playwright.CreateAsync();
    await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
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

        var task = method.Invoke(service, new object[]
            { "123456", new Progress<string>(_ => { }) }) as Task
            ?? throw new InvalidOperationException("Automatic OTP method did not return a Task.");

        var rejected = false;
        try
        {
            await task;
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }

        if (!rejected)
            throw new InvalidOperationException(
                "The OTP method should reject a registration page that has no OTP input.");

        var nationalId = await page.Locator("#national-id").InputValueAsync();
        if (nationalId.Length != 0)
            throw new InvalidOperationException(
                $"The OTP code was incorrectly entered into the national-ID field: '{nationalId}'.");
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
          <section id="date-picker-foreigner" data-input="birthDate">
            <div id="day" class="custom-dropdown"><span>روز</span></div>
            <div id="month" class="custom-dropdown"><span>ماه</span></div>
            <div id="year" class="custom-dropdown"><span>سال تولد</span></div>
          </section>
          <input id="birth-date-foreigner" name="birthDate" type="hidden" value="">
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
                  const parts = ['day','month','year'].map(id => document.querySelector('#'+id+' span').textContent.trim());
                  if (parts[0] !== 'روز' && parts[1] !== 'ماه' && parts[2] !== 'سال تولد') {
                    document.querySelector('#birth-date-foreigner').value = parts.join('/');
                  }
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

static async Task RunOfficialPortalMarkupRegressionAsync()
{
    const string html = """
        <!doctype html>
        <html lang="fa" dir="rtl">
        <head>
          <style>
            form.hidden { display: none !important; }
            .custom-select-wrapper { position: relative; width: 110px; height: 42px; }
            .custom-select { width: 100%; height: 100%; cursor: pointer; position: relative; }
            .custom-select > div:first-child { cursor: pointer; width: 100%; height: 100%; }
            .select-options { display: none; position: absolute; top: 100%; left: 0; z-index: 10; background: white; }
            .select-options.active { display: block; }
            .select-option { cursor: pointer; padding: 4px; }
          </style>
        </head>
        <body>
          <div id="iranian-btn" data-selected="true">ایرانی</div>
          <div id="foreigner-btn" data-selected="false">اتباع</div>

          <form id="register-iranian-form">
            <input id="national-id" name="nationalId" placeholder="کد ملی">
            <div id="date-picker-iranian"></div>
            <input id="birth-date-iranian" name="birthDate" type="hidden" value="">
            <input name="iranian" type="hidden" value="true">
          </form>

          <form id="register-foreigner-form" class="hidden">
            <input id="passport-code" name="passPortCode" maxlength="12"
                   placeholder="شماره گذرنامه/ فراگیر ناجا/ شناسه یکتا">
            <input id="shenasaei-code" name="shenasaeiCode" maxlength="12"
                   placeholder="کد فیدا">
            <div id="date-picker-foreigner" data-input="birthDate">
              <div class="custom-select-wrapper">
                <div id="day-control" class="custom-select">
                  <div class="placeholder">روز</div>
                  <div class="select-options">
                    <div class="select-option">15</div>
                  </div>
                </div>
              </div>
              <div class="custom-select-wrapper">
                <div id="month-control" class="custom-select">
                  <div class="placeholder">ماه</div>
                  <div class="select-options">
                    <div class="select-option">فروردین</div>
                    <div class="select-option">اردیبهشت</div>
                  </div>
                </div>
              </div>
              <div class="custom-select-wrapper">
                <div id="year-control" class="custom-select">
                  <div class="placeholder">سال تولد</div>
                  <div class="select-options">
                    <div class="select-option">1370</div>
                  </div>
                </div>
              </div>
            </div>
            <input id="birth-date-foreigner" name="birthDate" type="hidden" value="">
            <input name="iranian" type="hidden" value="false">
          </form>

          <script>
            document.querySelectorAll('#date-picker-foreigner .custom-select').forEach(control => {
              const trigger = control.querySelector('.placeholder');
              trigger.addEventListener('click', () => {
                control.querySelector('.select-options').classList.add('active');
              });
              control.querySelectorAll('.select-option').forEach(option => option.addEventListener('click', () => {
                trigger.textContent = option.textContent.trim();
                control.querySelector('.select-options').classList.remove('active');
                const values = [...document.querySelectorAll('#date-picker-foreigner .placeholder')].map(x => x.textContent.trim());
                if (values.every(x => x && !['روز','ماه','سال تولد'].includes(x))) {
                  document.getElementById('birth-date-foreigner').value = values.join('/');
                  document.getElementById('birth-date-foreigner').dispatchEvent(new Event('input', { bubbles: true }));
                  document.getElementById('birth-date-foreigner').dispatchEvent(new Event('change', { bubbles: true }));
                }
              }));
            });

            document.getElementById('foreigner-btn').addEventListener('click', () => {
              document.getElementById('foreigner-btn').setAttribute('data-selected', 'true');
              document.getElementById('iranian-btn').setAttribute('data-selected', 'false');
              document.getElementById('register-iranian-form').classList.add('hidden');
              document.getElementById('register-foreigner-form').classList.remove('hidden');
            });
          </script>
        </body>
        </html>
        """;

    using var playwright = await Playwright.CreateAsync();
    await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
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
        var openMethod = typeof(ChromeAutomationService).GetMethod(
            "OpenRegistrationForQueueAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Registration selection method was not found.");
        var openTask = openMethod.Invoke(service, new object[] { CancellationToken.None }) as Task
            ?? throw new InvalidOperationException("Registration selection did not return a Task.");
        await openTask;

        if (await page.Locator("#foreigner-btn").GetAttributeAsync("data-selected") != "true")
            throw new InvalidOperationException("The exact official foreigners tab did not become selected.");
        if (!await page.Locator("#register-foreigner-form").IsVisibleAsync() ||
            await page.Locator("#register-iranian-form").IsVisibleAsync())
            throw new InvalidOperationException("The portal did not switch from Iranian form to foreigners form.");

        var applicant = new Applicant
        {
            FullName = "Portal HTML Regression",
            UniqueId = "300012345678",
            FidaCode = "123456789012",
            BirthDate = "15/02/1370",
            MappingIndex = 0
        };

        await service.FillIdentityFormAsync(applicant, 0);

        var actualUnique = await page.Locator("#passport-code").InputValueAsync();
        var actualFida = await page.Locator("#shenasaei-code").InputValueAsync();
        if (actualUnique != applicant.UniqueId || actualFida != applicant.FidaCode)
            throw new InvalidOperationException(
                $"Official identity fields mismatch. Passport/unique='{actualUnique}', FIDA='{actualFida}'.");

        var actualBirthDate = await page.Locator("#birth-date-foreigner").InputValueAsync();
        if (actualBirthDate != "15/اردیبهشت/1370")
            throw new InvalidOperationException(
                $"Official hidden birth date mismatch. Expected 15/اردیبهشت/1370, got '{actualBirthDate}'.");
    }
    finally
    {
        await service.DisposeAsync();
    }
}

static async Task RunAuthenticatedSecondTabRegressionAsync()
{
    const string loginHtml = """
        <!doctype html><html lang="fa" dir="rtl"><body>
          <div>ورود به پنجره ملی</div>
          <label>کد پیامکی</label>
          <input id="otp" maxlength="6">
        </body></html>
        """;
    const string registerHtml = """
        <!doctype html><html lang="fa" dir="rtl"><body>
          <h1>درگاه یکپارچه احراز هویت</h1>
          <div class="tabs">
            <div>اتباع</div><div>ایرانی</div>
          </div>
          <input placeholder="کد ملی">
          <div>روز ماه سال تولد</div>
          <div>کد فیدا</div>
        </body></html>
        """;

    using var playwright = await Playwright.CreateAsync();
    await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
    var context = await browser.NewContextAsync();
    await context.RouteAsync("**/*", async route =>
    {
        var url = route.Request.Url;
        var body = url.Contains("/register", StringComparison.OrdinalIgnoreCase) ? registerHtml : loginHtml;
        await route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "text/html; charset=utf-8",
            Body = body
        });
    });

    var loginPage = await context.NewPageAsync();
    await loginPage.GotoAsync(ChromeAutomationService.LoginUrl);
    var registrationPage = await context.NewPageAsync();
    await registrationPage.GotoAsync(ChromeAutomationService.RegisterUrl);

    var service = new ChromeAutomationService();
    SetPrivateField(service, "_context", context);
    SetPrivateField(service, "_page", loginPage);

    try
    {
        var method = typeof(ChromeAutomationService).GetMethod(
            "TryAdoptAuthenticatedPortalPageAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Active authenticated tab detector was not found.");
        var task = method.Invoke(service, Array.Empty<object>()) as Task<bool>
            ?? throw new InvalidOperationException("Active tab detector did not return Task<bool>.");
        var adopted = await task;

        if (!adopted)
            throw new InvalidOperationException("The authenticated registration tab was not adopted.");
        if (!service.CurrentUrl.Contains("/register", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Expected active /register tab, got '{service.CurrentUrl}'.");
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
