using System.IO;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Playwright;
using DolatMan.Models;

namespace DolatMan.Services;

/// <summary>
/// Opens the official portal in a dedicated Chrome profile and assists with form entry.
/// CAPTCHA is solved by the operator, SMS OTP is entered in Chrome by the operator,
/// and only the explicitly authorized form flow is submitted automatically.
/// </summary>
public sealed class ChromeAutomationService : IAsyncDisposable
{
    public const string LoginUrl = "https://sso.my.gov.ir/login";
    public const string RegisterUrl = "https://sso.my.gov.ir/register";
    public const string DashboardUrl = "https://my.gov.ir/dashboard";
    public const string IndividualAccountUrl = "https://my.gov.ir/account/individual";

    private readonly string _profileDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DolatMan", "ChromeProfile");
    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    private IPage? _page;

    public bool IsOpen => _context is not null && _page is not null && !_page.IsClosed;
    public string CurrentUrl => IsOpen ? _page!.Url : "مرورگر هنوز باز نشده است";

    /// <summary>
    /// Opens the official sign-in page as the very first page in the program's Chrome window.
    /// The page is explicitly navigated and brought to the foreground instead of relying on
    /// Chrome's new-tab/start-page behaviour.
    /// </summary>
    public async Task<string> OpenLoginAsync()
    {
        await EnsureChromeAsync();

        if (_page is null || _page.IsClosed)
            _page = await _context!.NewPageAsync();

        await NavigateAsync(LoginUrl);
        await _page.BringToFrontAsync();

        // The portal currently presents a service-choice screen first. Continue through
        // the official "ورود به پنجره ملی" option before asking the operator for a mobile number.
        return await ContinueFromServiceChoiceAsync();
    }

    private async Task<string> ContinueFromServiceChoiceAsync()
    {
        RequirePage();
        var entry = _page!.Locator("text=ورود به پنجره ملی").First;

        // Give the site's client-side page a short period to render. If the direct mobile
        // login form is already shown, do not click anything else.
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (await IsMobileLoginPageAsync())
                return "صفحه ورود موبایل نمایش داده شد. شماره موبایل را وارد کنید؛ کپچا و پیامک را خودتان تکمیل کنید.";

            if (await entry.CountAsync() > 0 && await entry.IsVisibleAsync())
            {
                await entry.ClickAsync(new LocatorClickOptions { Timeout = 15000 });
                await _page.BringToFrontAsync();

                for (var wait = 0; wait < 40; wait++)
                {
                    if (await IsMobileLoginPageAsync())
                        return "گزینه «ورود به پنجره ملی» با موفقیت انتخاب شد. اکنون صفحه ورود موبایل باز است؛ شماره موبایل را وارد کنید و کپچا و پیامک را خودتان تکمیل کنید.";

                    await Task.Delay(500);
                }

                throw new InvalidOperationException(
                    "گزینه «ورود به پنجره ملی» کلیک شد، اما صفحه ورود موبایل تشخیص داده نشد. " +
                    "صفحه فعلی Chrome را بررسی کنید؛ در صورت نیاز ادامه را دستی انجام دهید.");
            }

            await Task.Delay(500);
        }

        throw new InvalidOperationException(
            "صفحه انتخاب خدمت باز شد، اما گزینه «ورود به پنجره ملی» پیدا نشد. " +
            "لطفاً در Chrome این گزینه را دستی انتخاب کنید؛ سپس شماره موبایل و کپچا را وارد کنید.");
    }

    private async Task<bool> IsMobileLoginPageAsync()
    {
        try
        {
            var body = await _page!.Locator("body").InnerTextAsync(new LocatorInnerTextOptions { Timeout = 2500 });
            var hasPhone = body.Contains("شماره موبایل", StringComparison.OrdinalIgnoreCase) ||
                           body.Contains("شماره همراه", StringComparison.OrdinalIgnoreCase) ||
                           body.Contains("تلفن همراه", StringComparison.OrdinalIgnoreCase);
            var hasCaptcha = body.Contains("کد امنیتی", StringComparison.OrdinalIgnoreCase);
            var hasSend = body.Contains("ارسال کد", StringComparison.OrdinalIgnoreCase);
            return hasPhone && hasCaptcha && hasSend;
        }
        catch
        {
            return false;
        }
    }

    public async Task FillMobileAsync(string mobile)
    {
        RequirePage();
        if (string.IsNullOrWhiteSpace(mobile)) throw new InvalidOperationException("شماره موبایل متقاضی ثبت نشده است.");

        var input = await FindByDescriptionAsync("input:visible, textarea:visible, [contenteditable='true']:visible, [role='textbox']:visible", new[] { "موبایل", "همراه", "تلفن", "mobile", "phone" });
        if (input is null)
        {
            var candidates = await GetEditableInputsAsync();
            input = candidates.FirstOrDefault(x => x.Type == "tel")?.Locator
                    ?? candidates.FirstOrDefault(x => x.Type is "text" or "number")?.Locator;
        }
        if (input is null) throw new InvalidOperationException("کادر شماره موبایل پیدا نشد. صفحه را بررسی کنید و شماره را دستی وارد کنید.");
        await input.FillAsync(ToAsciiDigits(mobile.Trim()));
    }

    public async Task OpenRegisterAfterManualLoginAsync()
    {
        RequirePage();
        var url = new Uri(_page!.Url);
        if (!url.Host.EndsWith("my.gov.ir", StringComparison.OrdinalIgnoreCase) ||
            url.AbsolutePath.Contains("/login", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("ابتدا ورود را در Chrome کامل کنید؛ کد امنیتی و کد پیامکی باید دستی تأیید شوند. سپس این دکمه را بزنید.");

        // The identity form itself may remain on sso.my.gov.ir/register.
        // If it is already open, do not reject it merely because its host starts with sso.
        if (url.AbsolutePath.Contains("/register", StringComparison.OrdinalIgnoreCase))
            return;

        await NavigateAsync(RegisterUrl);
    }

    public async Task<string> FillIdentityFormAsync(Applicant applicant, int mappingIndex)
    {
        RequirePage();
        var current = new Uri(_page!.Url);
        if (!current.Host.EndsWith("my.gov.ir", StringComparison.OrdinalIgnoreCase) ||
            !current.AbsolutePath.Contains("/register", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("ابتدا پس از ورود، صفحه ثبت اطلاعات اتباع را در Chrome باز کنید.");
        if (string.IsNullOrWhiteSpace(applicant.UniqueId) || string.IsNullOrWhiteSpace(applicant.FidaCode))
            throw new InvalidOperationException("شناسه یکتا و کد فیدا باید در پرونده متقاضی ذخیره شده باشند.");

        var firstValue = mappingIndex switch
        {
            0 => applicant.UniqueId,
            1 or 2 => applicant.FidaCode,
            _ => throw new InvalidOperationException("روش ورود معتبر انتخاب نشده است.")
        };
        var secondValue = mappingIndex switch
        {
            0 => applicant.FidaCode,
            1 => applicant.UniqueId,
            2 => applicant.FidaCode,
            _ => throw new InvalidOperationException("روش ورود معتبر انتخاب نشده است.")
        };

        var fieldSelector = "input:visible, textarea:visible, [contenteditable='true']:visible, [role='textbox']:visible";
        var first = await FindByDescriptionAsync(fieldSelector, new[] { "shenasaeiCode", "shenasaei-code", "passportCode", "passport-code", "شناسه", "یکتا", "گذرنامه", "فراگیر", "passport" });
        var second = await FindByDescriptionAsync(fieldSelector, new[] { "fidaCode", "fida-code", "کد فیدا", "فیدا", "fida" });

        var editable = await GetEditableInputsAsync();
        if (first is null || second is null)
        {
            // Only use positional fallback when the photographed form has exactly two editable
            // text inputs. If the DOM contains extra fields, guessing their order could put
            // identity data into the wrong field, so stop instead.
            if (editable.Count != 2)
                throw new InvalidOperationException(await BuildRegistrationDiagnosticAsync("دو کادر شناسه با اطمینان شناسایی نشدند؛ برای جلوگیری از ورود اطلاعات در فیلد اشتباه، عملیات متوقف شد."));
            first = editable[0].Locator;
            second = editable[1].Locator;
        }

        var expectedFirst = ToAsciiDigits(firstValue.Trim());
        var expectedSecond = ToAsciiDigits(secondValue.Trim());
        // Use real keyboard input rather than setting the value directly. Some portal
        // controls are framework-controlled and ignore a programmatic fill until key events fire.
        await first.ClickAsync();
        await first.FillAsync(string.Empty);
        await first.PressSequentiallyAsync(expectedFirst, new LocatorPressSequentiallyOptions { Timeout = 10000 });
        await second.ClickAsync();
        await second.FillAsync(string.Empty);
        await second.PressSequentiallyAsync(expectedSecond, new LocatorPressSequentiallyOptions { Timeout = 10000 });

        // Read back both values before any submission. If the site rejected a fill or its
        // controls changed, stop instead of sending an unverified identity form.
        var actualFirst = await ReadEditableValueAsync(first);
        var actualSecond = await ReadEditableValueAsync(second);
        if (NormalizeNumber(actualFirst) != NormalizeNumber(expectedFirst) ||
            NormalizeNumber(actualSecond) != NormalizeNumber(expectedSecond))
            throw new InvalidOperationException("مقادیر دو کادر شناسه پس از ورود با مقدار مورد انتظار یکسان نیستند؛ فرم ثبت نشد.");

        var dateResult = await FillBirthDateAsync(applicant.BirthDate);
        return $"دو کادر شناسایی وارد و کنترل شدند. {dateResult}";
    }


    /// <summary>
    /// Runs one applicant from the login gateway through the account evidence export.
    /// The operator solves the CAPTCHA and enters the received SMS code in DolatMan.
    /// The code is typed into the official portal; it is not read from messages or stored.
    /// Identity mapping is taken only from the applicant's saved selection.
    /// </summary>
    public async Task<string> ProcessApplicantFlowAsync(
        Applicant applicant,
        Func<byte[]?, Task<string?>> requestCaptcha,
        Func<Task<string?>> requestOtp,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(applicant.Mobile))
            throw new InvalidOperationException($"شماره موبایل متقاضی «{applicant.FullName}» ثبت نشده است.");
        if (string.IsNullOrWhiteSpace(applicant.FullName))
            throw new InvalidOperationException("نام متقاضی ثبت نشده است.");

        cancellationToken.ThrowIfCancellationRequested();
        progress.Report($"پرونده {applicant.FullName}: باز کردن صفحه ورود دولت من...");
        await OpenLoginAsync();

        cancellationToken.ThrowIfCancellationRequested();
        progress.Report("ورود به پنجره ملی انجام شد؛ شماره موبایل در حال وارد شدن است...");
        await FillMobileAsync(applicant.Mobile);

        progress.Report("در حال دریافت تصویر کد امنیتی برای نمایش در DolatMan...");
        var captchaImage = await CaptureCaptchaImageAsync();
        var captcha = await requestCaptcha(captchaImage);
        if (string.IsNullOrWhiteSpace(captcha))
            throw new OperationCanceledException("ورود کد امنیتی لغو شد.", cancellationToken);

        await SubmitCaptchaAndRequestOtpAsync(captcha.Trim());
        progress.Report("درخواست کد پیامکی از سامانه انجام شد؛ منتظر وارد کردن کد در برنامه هستیم...");
        var otp = await requestOtp();
        if (string.IsNullOrWhiteSpace(otp))
            throw new OperationCanceledException("ورود کد پیامکی لغو شد.", cancellationToken);
        await EnterOtpAndVerifyAsync(otp.Trim());
        progress.Report("کد پیامکی در فرم سامانه وارد شد؛ در حال بررسی نتیجه ورود...");
        await WaitForAuthenticatedPortalAsync(TimeSpan.FromMinutes(5), cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        progress.Report("ورود شناسایی شد؛ باز کردن فرم ثبت اطلاعات اتباع...");
        await OpenRegistrationForQueueAsync(cancellationToken);

        progress.Report("در حال پر کردن اطلاعات هویتی طبق روش ذخیره‌شده برای این متقاضی...");
        var fillMessage = await FillIdentityFormAsync(applicant, applicant.MappingIndex);
        if (fillMessage.Contains("دستی", StringComparison.OrdinalIgnoreCase) ||
            fillMessage.Contains("قابل شناسایی نبود", StringComparison.OrdinalIgnoreCase) ||
            fillMessage.Contains("خودتان", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("فرم کامل به‌صورت خودکار پر نشد: " + fillMessage);
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress.Report("اطلاعات وارد شد؛ در حال ثبت فرم و انتظار برای داشبورد...");
        await ClickRegisterSubmitAsync();
        await WaitForDashboardAsync(TimeSpan.FromMinutes(2), cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        progress.Report("داشبورد باز شد؛ ورود به حساب حقیقی...");
        await OpenIndividualAccountFromDashboardAsync(cancellationToken);

        progress.Report("صفحه حساب حقیقی باز شد؛ در حال ذخیره PDF و تصویر...");
        var exportMessage = await SaveIndividualAccountAsync(applicant);
        progress.Report($"پرونده {applicant.FullName}: خروجی ذخیره شد.");
        return exportMessage;
    }

    private async Task SubmitCaptchaAndRequestOtpAsync(string captcha)
    {
        RequirePage();
        var captchaInput = await FindByDescriptionAsync("input:visible, textarea:visible, [contenteditable='true']:visible, [role='textbox']:visible", new[] { "کد امنیتی", "captcha", "security" });
        if (captchaInput is null)
        {
            var inputs = await GetEditableInputsAsync();
            var mobileInput = await FindByDescriptionAsync("input:visible, textarea:visible, [contenteditable='true']:visible, [role='textbox']:visible", new[] { "موبایل", "همراه", "تلفن", "mobile", "phone" });
            if (inputs.Count == 2 && mobileInput is not null)
                captchaInput = inputs[1].Locator;
        }

        if (captchaInput is null)
            throw new InvalidOperationException("کادر کد امنیتی در صفحه ورود پیدا نشد. برای جلوگیری از ورود اشتباه، ادامه متوقف شد.");

        var normalizedCaptcha = ToAsciiDigits(captcha.Trim());
        if (normalizedCaptcha.Length == 0)
            throw new InvalidOperationException("کد امنیتی خالی است؛ چیزی برای ارسال وجود ندارد.");

        // Some login pages use one input per CAPTCHA digit and listen for real keyboard
        // events. Prefer those one-character boxes and type each character separately.
        var digitBoxes = _page!.Locator("input:visible[maxlength='1']");
        var digitBoxCount = await digitBoxes.CountAsync();
        if (digitBoxCount >= normalizedCaptcha.Length)
        {
            for (var i = 0; i < normalizedCaptcha.Length; i++)
            {
                var digitBox = digitBoxes.Nth(i);
                await digitBox.FillAsync(string.Empty);
                await digitBox.PressSequentiallyAsync(normalizedCaptcha[i].ToString(),
                    new LocatorPressSequentiallyOptions { Timeout = 5000 });
            }
        }
        else
        {
            // Single-field CAPTCHA: send keyboard events character by character rather
            // than assigning the whole value with FillAsync.
            await captchaInput.FillAsync(string.Empty);
            await captchaInput.PressSequentiallyAsync(normalizedCaptcha,
                new LocatorPressSequentiallyOptions { Timeout = 10000 });
        }

        var sendButton = await FindClickableByTextAsync(new[] { "ارسال کد" });
        if (sendButton is null)
            throw new InvalidOperationException("دکمه «ارسال کد» پیدا نشد. کد امنیتی وارد شد؛ صفحه Chrome را بررسی کنید.");
        await sendButton.ClickAsync(new LocatorClickOptions { Timeout = 15000 });
        // Bring the official Chrome window forward so the operator can enter the SMS code there.
        await _page!.BringToFrontAsync();
    }

    private async Task EnterOtpAndVerifyAsync(string otp)
    {
        RequirePage();
        var code = ToAsciiDigits(otp.Trim());
        if (code.Length < 4 || code.Length > 8)
            throw new InvalidOperationException("کد پیامکی باید بین ۴ تا ۸ رقم باشد.");

        // Prefer split one-character OTP boxes before looking for a single labelled input.
        // Many identity portals render one box per digit.
        var splitBoxes = _page!.Locator("input:visible[maxlength='1']");
        var splitCount = await splitBoxes.CountAsync();
        if (splitCount >= code.Length)
        {
            for (var i = 0; i < code.Length; i++)
            {
                var box = splitBoxes.Nth(i);
                await box.ClickAsync();
                await box.FillAsync(string.Empty);
                await box.PressSequentiallyAsync(code[i].ToString(), new LocatorPressSequentiallyOptions { Timeout = 5000 });
            }
        }
        else
        {
            var selector = "input:visible, textarea:visible, [contenteditable='true']:visible, [role='textbox']:visible";
            var otpInput = await FindByDescriptionAsync(selector,
                new[] { "کد تایید", "کد تأیید", "کد یکبار مصرف", "کد یک‌بار مصرف", "کد پیامکی", "otp", "verification", "one-time" });
            if (otpInput is null)
            {
                var candidates = await GetEditableInputsAsync();
                var codeBoxes = candidates.Where(x => x.Type is "tel" or "number" or "text" or "password").ToList();
                if (codeBoxes.Count == 1) otpInput = codeBoxes[0].Locator;
            }

            if (otpInput is null)
                throw new InvalidOperationException("کادر کد پیامکی پیدا نشد؛ برای جلوگیری از ورود کد در فیلد اشتباه، عملیات متوقف شد.");

            await otpInput.ClickAsync();
            await otpInput.FillAsync(string.Empty);
            await otpInput.PressSequentiallyAsync(code, new LocatorPressSequentiallyOptions { Timeout = 10000 });
            var actual = ToAsciiDigits(await otpInput.InputValueAsync());
            if (actual != code)
                throw new InvalidOperationException("کد پیامکی در کادر سامانه درست ثبت نشد؛ کد را در Chrome بررسی کنید.");
        }

        var verify = await FindClickableByTextAsync(new[] { "تأیید", "تایید", "ورود", "ادامه" });
        if (verify is null)
            throw new InvalidOperationException("کد در فرم وارد شد، اما دکمه تأیید/ورود پیدا نشد. لطفاً دکمه را در Chrome دستی بزنید.");
        await verify.ClickAsync(new LocatorClickOptions { Timeout = 15000 });
        await _page!.BringToFrontAsync();
    }

    private async Task WaitForAuthenticatedPortalAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_page is null || _page.IsClosed)
                throw new InvalidOperationException("پنجره Chrome بسته شد؛ ورود متوقف شد.");

            if (Uri.TryCreate(_page.Url, UriKind.Absolute, out var current) &&
                current.Host.EndsWith("my.gov.ir", StringComparison.OrdinalIgnoreCase) &&
                !current.AbsolutePath.Contains("/login", StringComparison.OrdinalIgnoreCase) &&
                (current.AbsolutePath.Contains("/register", StringComparison.OrdinalIgnoreCase) ||
                 current.AbsolutePath.Contains("/dashboard", StringComparison.OrdinalIgnoreCase) ||
                 current.AbsolutePath.Contains("/account", StringComparison.OrdinalIgnoreCase)))
                return;

            var body = await ReadBodyTextSafelyAsync();
            var securityCodeRejected = body.Contains("کد امنیتی", StringComparison.OrdinalIgnoreCase) &&
                                       ContainsAny(body, "اشتباه", "صحیح نیست", "نامعتبر", "نادرست");
            var oneTimeCodeRejected = ContainsAny(body, "کد تأیید", "کد تایید", "کد پیامکی", "کد یکبار مصرف") &&
                                      ContainsAny(body, "اشتباه", "صحیح نیست", "نامعتبر", "نادرست");
            if (securityCodeRejected || oneTimeCodeRejected)
                throw new InvalidOperationException("سامانه کد امنیتی یا کد پیامکی را نپذیرفت. کد و پیام خطا را بررسی و پرونده را دوباره اجرا کنید.");
            if (body.Contains("کد فیدا", StringComparison.OrdinalIgnoreCase) &&
                (body.Contains("شناسه", StringComparison.OrdinalIgnoreCase) || body.Contains("اتباع", StringComparison.OrdinalIgnoreCase)))
                return;

            await Task.Delay(700, cancellationToken);
        }

        throw new TimeoutException(
            "ورود در زمان مجاز کامل نشد. کد پیامکی را در Chrome وارد و تأیید کنید. " +
            "اگر خطای سامانه یا کد اشتباه نمایش داده می‌شود، آن را برطرف کرده و اجرای پرونده را دوباره آغاز کنید.");
    }

    private async Task OpenRegistrationForQueueAsync(CancellationToken cancellationToken)
    {
        RequirePage();
        if (!new Uri(_page!.Url).AbsolutePath.Contains("/register", StringComparison.OrdinalIgnoreCase))
            await NavigateAsync(RegisterUrl);

        try { await _page!.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions { Timeout = 20000 }); }
        catch { /* SPA transitions may not emit a full load event. */ }

        // IMPORTANT: the portal commonly opens with the «اتباع» tab already selected.
        // Clicking an already-selected tab can toggle/switch the panel and hide its inputs.
        // First verify the real visible identity fields; click the tab only when they are absent.
        if (await IsIdentityFormReadyAsync())
            return;

        var clicked = false;
        foreach (var frame in _page!.Frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                clicked = await frame.EvaluateAsync<bool>("""() => {
                    const norm = s => (s || '').normalize('NFKC')
                      .replace(/[\s\u200c\u200f\u0640]/g, '')
                      .replace(/[ي]/g, 'ی').replace(/[ك]/g, 'ک');
                    const nodes = [...document.querySelectorAll('[role=tab],button,[role=button],a,label,[tabindex]')];
                    const target = nodes.find(el => {
                      const text = norm(el.innerText || el.textContent || el.getAttribute('aria-label'));
                      const visible = !!(el.getClientRects().length) && getComputedStyle(el).visibility !== 'hidden';
                      return visible && text === 'اتباع' && !el.disabled;
                    });
                    if (!target) return false;
                    const actionable = target.closest('[role=tab],button,[role=button],a,label') || target;
                    actionable.click();
                    return true;
                }""");
            }
            catch { clicked = false; }
            if (clicked) break;
        }

        if (!clicked && !await IsIdentityFormReadyAsync())
            throw new InvalidOperationException(await BuildRegistrationDiagnosticAsync(
                "گزینه قابل کلیک «اتباع» پیدا نشد و فرم اتباع نیز نمایش داده نشده است."));

        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < TimeSpan.FromSeconds(60))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsIdentityFormReadyAsync()) return;
            await Task.Delay(400, cancellationToken);
        }
        throw new TimeoutException(await BuildRegistrationDiagnosticAsync(
            "پس از انتخاب «اتباع»، فیلدهای شناسه یکتا و کد فیدا در صفحه ظاهر نشدند."));
    }

    private async Task<bool> IsIdentityFormReadyAsync()
    {
        // Use the actual visible field labels/placeholders, not page text alone: hidden
        // templates and inactive tabs can contain the same Persian words.
        const string selector = "input:visible, textarea:visible, [contenteditable='true']:visible, [role='textbox']:visible";
        var first = await FindByDescriptionAsync(selector,
            new[] { "شناسه یکتا", "شناسه‌یکتا", "شماره گذرنامه", "شماره فراگیر", "shenasaeiCode", "passportCode" });
        var second = await FindByDescriptionAsync(selector,
            new[] { "کد فیدا", "کدفیدا", "fidaCode", "fida-code" });
        if (first is not null && second is not null) return true;

        var body = NormalizeForMatch(await ReadBodyTextSafelyAsync());
        var editable = await GetEditableInputsAsync();
        return body.Contains(NormalizeForMatch("کد فیدا"), StringComparison.Ordinal) &&
               (body.Contains(NormalizeForMatch("شناسه یکتا"), StringComparison.Ordinal) ||
                body.Contains(NormalizeForMatch("شماره گذرنامه"), StringComparison.Ordinal)) &&
               editable.Count >= 2;
    }

    private async Task<string> BuildRegistrationDiagnosticAsync(string reason)
    {
        var body = await ReadBodyTextSafelyAsync();
        var inputs = await GetEditableInputsAsync();
        var details = new List<string>();
        foreach (var item in inputs.Take(8))
        {
            var desc = await DescribeAsync(item.Locator);
            details.Add(string.IsNullOrWhiteSpace(desc) ? $"input({item.Type})" : desc.Trim());
        }
        var buttonDetails = new List<string>();
        foreach (var frame in _page!.Frames)
        {
            var buttons = frame.Locator("button:visible, [role=tab]:visible, [role=combobox]:visible, [aria-haspopup]:visible");
            var count = Math.Min(await buttons.CountAsync(), 12);
            for (var i = 0; i < count; i++)
            {
                try
                {
                    var label = (await buttons.Nth(i).InnerTextAsync()).Trim();
                    if (!string.IsNullOrWhiteSpace(label)) buttonDetails.Add(Truncate(label, 45));
                }
                catch { }
            }
        }
        return reason + "\nنشانی: " + (_page?.Url ?? "نامشخص") +
               "\nتعداد کادرهای قابل ویرایش: " + inputs.Count +
               "\nمشخصات کادرها: " + (details.Count == 0 ? "هیچ‌کدام" : string.Join(" | ", details)) +
               "\nدکمه‌ها/فهرست‌های قابل مشاهده: " + (buttonDetails.Count == 0 ? "هیچ‌کدام" : string.Join(" | ", buttonDetails.Distinct())) +
               "\nبخشی از متن صفحه: " + Truncate(body, 500);
    }

    private async Task ClickRegisterSubmitAsync()
    {
        var submit = await FindClickableByTextAsync(new[] { "ثبت اطلاعات" });
        if (submit is null)
            throw new InvalidOperationException("دکمه «ثبت اطلاعات» پیدا نشد؛ فرم ارسال نشد.");
        await submit.ClickAsync(new LocatorClickOptions { Timeout = 15000 });
    }

    private async Task WaitForDashboardAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        string lastBody = "";
        while (DateTime.UtcNow - started < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_page is null || _page.IsClosed)
                throw new InvalidOperationException("پنجره Chrome پیش از رسیدن به داشبورد بسته شد.");

            if (Uri.TryCreate(_page.Url, UriKind.Absolute, out var current) &&
                current.Host.Equals("my.gov.ir", StringComparison.OrdinalIgnoreCase) &&
                current.AbsolutePath.Contains("/dashboard", StringComparison.OrdinalIgnoreCase))
                return;

            lastBody = await ReadBodyTextSafelyAsync();
            if (ContainsAny(lastBody, "نامعتبر", "خطا در ثبت", "یافت نشد", "مغایرت", "تکراری"))
                throw new InvalidOperationException("سامانه پیام خطا یا مغایرت نمایش داده است؛ تلاش خودکار متوقف شد. متن صفحه: " + Truncate(lastBody, 500));

            await Task.Delay(700, cancellationToken);
        }

        throw new TimeoutException("پس از ثبت اطلاعات، داشبورد تأیید نشد. برنامه روش دوم یا سوم را خودکار امتحان نمی‌کند. نشانی فعلی: " + CurrentUrl + "\n" + Truncate(lastBody, 400));
    }

    private async Task OpenIndividualAccountFromDashboardAsync(CancellationToken cancellationToken)
    {
        RequirePage();
        var current = new Uri(_page!.Url);
        if (!current.Host.Equals("my.gov.ir", StringComparison.OrdinalIgnoreCase) ||
            !current.AbsolutePath.Contains("/dashboard", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("پیش از ورود به حساب حقیقی، رسیدن به داشبورد باید تأیید شود.");

        var accountButton = await FindClickableByTextAsync(new[] { "ورود به حساب کاربری" });
        if (accountButton is not null)
            await accountButton.ClickAsync(new LocatorClickOptions { Timeout = 15000 });
        else
            await NavigateAsync(IndividualAccountUrl);

        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < TimeSpan.FromSeconds(40))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_page is not null && !_page.IsClosed &&
                Uri.TryCreate(_page.Url, UriKind.Absolute, out var uri) &&
                uri.Host.Equals("my.gov.ir", StringComparison.OrdinalIgnoreCase) &&
                uri.AbsolutePath.Contains("/account/individual", StringComparison.OrdinalIgnoreCase))
            {
                var body = await ReadBodyTextSafelyAsync();
                if (ContainsAny(body, "حساب", "هویتی", "کاربر حقیقی")) return;
            }
            await Task.Delay(500, cancellationToken);
        }
        throw new TimeoutException("با کلیک روی «ورود به حساب کاربری»، صفحه حساب حقیقی باز نشد.");
    }

    /// <summary>
    /// Closes the authenticated Chrome context and deletes its temporary profile so the next
    /// applicant starts with a clean browser session and cannot inherit the previous account.
    /// </summary>
    public async Task ResetSessionForNextApplicantAsync()
    {
        if (_context is not null)
        {
            try { await _context.ClearCookiesAsync(); } catch { }
            try { await _context.CloseAsync(); } catch { }
            _context = null;
            _page = null;
        }
        _playwright?.Dispose();
        _playwright = null;

        try
        {
            if (Directory.Exists(_profileDirectory))
                Directory.Delete(_profileDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            throw new IOException("نشست Chrome بسته شد، اما پاک‌سازی پروفایل موقت کامل نشد. پیش از اجرای متقاضی بعدی، برنامه را دوباره اجرا کنید.", ex);
        }
    }

    private async Task<ILocator?> FindClickableByTextAsync(string[] tokens)
    {
        RequirePage();
        var candidates = _page!.Locator("button:visible, input[type='submit']:visible, [role='button']:visible, a:visible");
        var count = await candidates.CountAsync();
        for (var i = 0; i < count; i++)
        {
            var item = candidates.Nth(i);
            string label;
            var tag = await item.EvaluateAsync<string>("el => el.tagName.toLowerCase()");
            if (tag == "input") label = await item.GetAttributeAsync("value") ?? "";
            else label = await item.InnerTextAsync();
            if (tokens.Any(token => label.Contains(token, StringComparison.OrdinalIgnoreCase)))
                return item;
        }
        return null;
    }

    private async Task<string> ReadBodyTextSafelyAsync()
    {
        try { return await _page!.Locator("body").InnerTextAsync(new LocatorInnerTextOptions { Timeout = 3000 }); }
        catch { return ""; }
    }

    private static bool ContainsAny(string text, params string[] tokens)
        => tokens.Any(token => text.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static string Truncate(string value, int maximum)
        => value.Length <= maximum ? value : value[..maximum] + "…";

    public async Task OpenDashboardAsync()
    {
        RequirePage();
        await NavigateAsync(DashboardUrl);
    }

    public async Task OpenIndividualAccountAsync()
    {
        RequirePage();
        var host = new Uri(_page!.Url).Host;
        if (!host.EndsWith("my.gov.ir", StringComparison.OrdinalIgnoreCase) ||
            host.StartsWith("sso.", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("ابتدا با حساب خود وارد سامانه دولت من شوید و به دامنه اصلی my.gov.ir برسید.");
        await NavigateAsync(IndividualAccountUrl);
    }

    public async Task<string> SaveIndividualAccountAsync(Applicant applicant)
    {
        RequirePage();
        var uri = new Uri(_page!.Url);
        if (!uri.Host.EndsWith("my.gov.ir", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.Contains("/account/individual", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("برای ذخیره مدرک، ابتدا صفحه حساب کاربری حقیقی را باز کنید.");

        var nameParts = applicant.FullName.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeForMatch).Where(part => part.Length >= 2).ToArray();
        string bodyText = "";
        var profileVerified = false;
        var verifyStarted = DateTime.UtcNow;
        while (DateTime.UtcNow - verifyStarted < TimeSpan.FromSeconds(30))
        {
            bodyText = await _page.Locator("body").InnerTextAsync(new() { Timeout = 8000 });
            var hasAccountContent = bodyText.Contains("حساب", StringComparison.OrdinalIgnoreCase) ||
                                    bodyText.Contains("هویتی", StringComparison.OrdinalIgnoreCase) ||
                                    bodyText.Contains("کاربر حقیقی", StringComparison.OrdinalIgnoreCase);
            var normalizedBody = NormalizeForMatch(bodyText);
            profileVerified = hasAccountContent && nameParts.Length > 0 &&
                              nameParts.All(part => normalizedBody.Contains(part, StringComparison.Ordinal));
            if (profileVerified) break;
            await Task.Delay(600);
        }

        if (!profileVerified)
            throw new InvalidOperationException("صفحه حساب باز شد، اما نام متقاضی در اطلاعات قابل مشاهده با پرونده تطبیق داده نشد. برای جلوگیری از ذخیره مدرک فرد اشتباه، خروجی و ادامه صف متوقف شد.");

        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DolatMan", "خروجی متقاضیان");
        var folderName = applicant.SafeFolderName + "_" + applicant.Id[..Math.Min(6, applicant.Id.Length)];
        var folder = Path.Combine(root, folderName, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(folder);
        var pngPath = Path.Combine(folder, "حساب-کاربری.png");
        var pdfPath = Path.Combine(folder, "حساب-کاربری.pdf");

        await _page.ScreenshotAsync(new PageScreenshotOptions { Path = pngPath, FullPage = true, Animations = ScreenshotAnimations.Disabled });
        CreatePdfFromPng(pngPath, pdfPath);
        return $"فایل PDF و تصویر با موفقیت ذخیره شدند.\nپوشه خروجی: {folder}";
    }

    private static void CreatePdfFromPng(string pngPath, string pdfPath)
    {
        using var pngStream = File.OpenRead(pngPath);
        var decoder = new PngBitmapDecoder(pngStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var rgb = new FormatConvertedBitmap(frame, PixelFormats.Rgb24, null, 0);
        var jpegEncoder = new JpegBitmapEncoder { QualityLevel = 90 };
        jpegEncoder.Frames.Add(BitmapFrame.Create(rgb));
        using var jpegStream = new MemoryStream();
        jpegEncoder.Save(jpegStream);
        var jpeg = jpegStream.ToArray();

        var landscape = frame.PixelWidth / (double)frame.PixelHeight > (595.0 / 842.0);
        var pageWidth = landscape ? 842.0 : 595.0;
        var pageHeight = landscape ? 595.0 : 842.0;
        var scale = Math.Min(pageWidth / frame.PixelWidth, pageHeight / frame.PixelHeight);
        var drawWidth = frame.PixelWidth * scale;
        var drawHeight = frame.PixelHeight * scale;
        var x = (pageWidth - drawWidth) / 2.0;
        var y = (pageHeight - drawHeight) / 2.0;
        var contentText = FormattableString.Invariant(
            $"q\n{drawWidth:0.###} 0 0 {drawHeight:0.###} {x:0.###} {y:0.###} cm\n/Im0 Do\nQ\n");
        var content = System.Text.Encoding.ASCII.GetBytes(contentText);

        using var output = new FileStream(pdfPath, FileMode.Create, FileAccess.Write, FileShare.None);
        var offsets = new List<long> { 0 };
        WritePdfAscii(output, "%PDF-1.4\n%DolatMan\n");

        WritePdfObject(output, offsets, 1, "<< /Type /Catalog /Pages 2 0 R >>");
        WritePdfObject(output, offsets, 2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        var mediaBox = FormattableString.Invariant($"[0 0 {pageWidth:0.###} {pageHeight:0.###}]");
        WritePdfObject(output, offsets, 3,
            $"<< /Type /Page /Parent 2 0 R /MediaBox {mediaBox} /Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>");

        offsets.Add(output.Position);
        WritePdfAscii(output, $"4 0 obj\n<< /Type /XObject /Subtype /Image /Width {frame.PixelWidth} /Height {frame.PixelHeight} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n");
        output.Write(jpeg, 0, jpeg.Length);
        WritePdfAscii(output, "\nendstream\nendobj\n");

        offsets.Add(output.Position);
        WritePdfAscii(output, $"5 0 obj\n<< /Length {content.Length} >>\nstream\n");
        output.Write(content, 0, content.Length);
        WritePdfAscii(output, "endstream\nendobj\n");

        var xrefPosition = output.Position;
        WritePdfAscii(output, "xref\n0 6\n0000000000 65535 f \n");
        for (var i = 1; i <= 5; i++)
            WritePdfAscii(output, offsets[i].ToString("0000000000", CultureInfo.InvariantCulture) + " 00000 n \n");
        WritePdfAscii(output, $"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xrefPosition}\n%%EOF\n");
    }

    private static void WritePdfObject(Stream output, List<long> offsets, int objectNumber, string body)
    {
        offsets.Add(output.Position);
        WritePdfAscii(output, $"{objectNumber} 0 obj\n{body}\nendobj\n");
    }

    private static void WritePdfAscii(Stream output, string text)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(text);
        output.Write(bytes, 0, bytes.Length);
    }

    public async Task<string> GetPageStatusAsync()
    {
        RequirePage();
        var title = await _page!.TitleAsync();
        var url = _page.Url;
        return $"عنوان صفحه: {title}\nنشانی: {url}";
    }

    private async Task EnsureChromeAsync()
    {
        if (IsOpen) return;
        if (_context is not null)
        {
            _page = _context.Pages.FirstOrDefault(page => !page.IsClosed)
                    ?? await _context.NewPageAsync();
            return;
        }
        Directory.CreateDirectory(_profileDirectory);
        _playwright ??= await Playwright.CreateAsync();
        try
        {
            _context = await _playwright.Chromium.LaunchPersistentContextAsync(_profileDirectory,
                new BrowserTypeLaunchPersistentContextOptions
                {
                    Channel = "chrome",
                    // Keep the real Chrome window visible so the operator can inspect the official portal,
                    // CAPTCHA, OTP and any validation message while DolatMan controls the page.
                    Headless = false,
                    ViewportSize = new ViewportSize { Width = 1440, Height = 1000 },
                    Args = new[]
                    {
                        "--disable-gpu",
                        "--no-first-run",
                        "--no-default-browser-check"
                    }
                });
        }
        catch (PlaywrightException ex)
        {
            throw new InvalidOperationException(
                "اجرای Google Chrome ممکن نشد. مطمئن شوید Chrome روی ویندوز نصب است و نسخه جاری آن قابل اجراست.\n" + ex.Message, ex);
        }

        _page = _context.Pages.FirstOrDefault() ?? await _context.NewPageAsync();
    }

    private async Task NavigateAsync(string url)
    {
        RequirePage();

        try
        {
            // Commit waits only until the browser has received the navigation response.
            // This avoids hanging on pages that keep network requests open after navigation.
            await _page!.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.Commit,
                Timeout = 90000
            });

            await _page.BringToFrontAsync();

            if (string.IsNullOrWhiteSpace(_page.Url) ||
                _page.Url.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Chrome باز شد، اما صفحه مقصد بارگذاری نشد. نشانی درخواستی: {url}");
            }
        }
        catch (PlaywrightException ex)
        {
            throw new InvalidOperationException(
                $"Chrome اجرا شد، اما باز کردن صفحه زیر ناموفق بود:\n{url}\n\n" +
                $"نشانی فعلی Chrome: {_page!.Url}\n" +
                "اتصال اینترنت، دسترسی به سامانه و باز بودن پنجره Chrome را بررسی کنید. " +
                "سپس دوباره دکمه «شروع کار با Chrome» را بزنید.\n\nجزئیات فنی: " + ex.Message, ex);
        }
    }

    private void RequirePage()
    {
        if (!IsOpen) throw new InvalidOperationException("ابتدا دکمه «باز کردن Chrome» را بزنید.");
    }

    private async Task<ILocator?> FindByDescriptionAsync(string selector, string[] tokens)
    {
        RequirePage();
        foreach (var frame in _page!.Frames)
        {
            var all = frame.Locator(selector);
            var count = await all.CountAsync();
            for (var i = 0; i < count; i++)
            {
                var item = all.Nth(i);
                if (!await item.IsVisibleAsync()) continue;
                var description = await DescribeAsync(item);
                if (tokens.Any(t => description.Contains(t, StringComparison.OrdinalIgnoreCase)))
                    return item;
            }
        }
        return null;
    }

    private async Task<List<InputCandidate>> GetEditableInputsAsync()
    {
        var result = new List<InputCandidate>();
        foreach (var frame in _page!.Frames)
        {
            var inputs = frame.Locator("input:visible, textarea:visible, [contenteditable='true']:visible, [role='textbox']:visible");
            var count = await inputs.CountAsync();
            for (var i = 0; i < count; i++)
            {
                var item = inputs.Nth(i);
                if (!await item.IsVisibleAsync()) continue;
                var tag = await item.EvaluateAsync<string>("el => el.tagName.toLowerCase()");
                var type = (await item.GetAttributeAsync("type") ?? (tag == "textarea" ? "textarea" : "text")).ToLowerInvariant();
                if (type is "hidden" or "submit" or "button" or "checkbox" or "radio" or "file" or "image") continue;
                if (await item.IsDisabledAsync()) continue;
                result.Add(new InputCandidate(item, type));
            }
        }
        return result;
    }

    private async Task<byte[]?> CaptureCaptchaImageAsync()
    {
        RequirePage();
        foreach (var frame in _page!.Frames)
        {
            var images = frame.Locator("img:visible, canvas:visible");
            var count = await images.CountAsync();
            for (var i = 0; i < count; i++)
            {
                var img = images.Nth(i);
                var description = string.Join(" ", new[] {
                    await img.GetAttributeAsync("alt"), await img.GetAttributeAsync("title"),
                    await img.GetAttributeAsync("id"), await img.GetAttributeAsync("class"),
                    await img.GetAttributeAsync("src"), await img.GetAttributeAsync("data-testid")
                }.Where(x => !string.IsNullOrWhiteSpace(x)));
                var nearby = await img.EvaluateAsync<string>("el => (el.parentElement?.innerText || '') + ' ' + (el.parentElement?.parentElement?.innerText || '')");
                if (HasAny((description + " " + nearby).ToLowerInvariant(), "captcha", "کد امنیتی", "security", "کد تصویری"))
                    return await img.ScreenshotAsync(new LocatorScreenshotOptions { Timeout = 7000 });
            }
        }
        // Do not guess from arbitrary page images: a missing CAPTCHA image is reported clearly.
        return null;
    }

    private static async Task<string> DescribeDateControlAsync(ILocator locator)
    {
        var direct = string.Join(" ", new[]
        {
            await locator.GetAttributeAsync("placeholder"),
            await locator.GetAttributeAsync("name"),
            await locator.GetAttributeAsync("id"),
            await locator.GetAttributeAsync("aria-label"),
            await locator.GetAttributeAsync("title")
        }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var labels = await locator.EvaluateAsync<string>(
            "el => (el.labels ? Array.from(el.labels).map(x => x.innerText).join(' ') : '')");
        return direct + " " + labels;
    }

    private static async Task<string> DescribeAsync(ILocator locator)
    {
        var direct = string.Join(" ", new[]
        {
            await locator.GetAttributeAsync("placeholder"),
            await locator.GetAttributeAsync("name"),
            await locator.GetAttributeAsync("id"),
            await locator.GetAttributeAsync("aria-label"),
            await locator.GetAttributeAsync("title")
        }.Where(x => !string.IsNullOrWhiteSpace(x)));

        var labels = await locator.EvaluateAsync<string>(
            "el => (el.labels ? Array.from(el.labels).map(x => x.innerText).join(' ') : '')");
        return direct + " " + labels;
    }

    private static async Task<string> ReadEditableValueAsync(ILocator control)
    {
        var tag = await control.EvaluateAsync<string>("el => el.tagName.toLowerCase()");
        var isContentEditable = await control.GetAttributeAsync("contenteditable");
        var role = await control.GetAttributeAsync("role");
        if (tag is "input" or "textarea" || role == "textbox" || isContentEditable == "true")
        {
            try { return await control.InputValueAsync(); }
            catch { return await control.InnerTextAsync(); }
        }
        return await control.InnerTextAsync();
    }

    private async Task<string> FillBirthDateAsync(string dateText)
    {
        if (!TryParseDate(dateText, out var day, out var month, out var year))
            return "قالب تاریخ تولد مشخص نیست؛ تاریخ را خودتان در Chrome وارد کنید (روز/ماه/سال).";

        var controls = _page!.Locator("input:visible, select:visible");
        var count = await controls.CountAsync();
        ILocator? dayControl = null, monthControl = null, yearControl = null;
        var selects = new List<(ILocator Locator, int OptionCount)>();

        for (var i = 0; i < count; i++)
        {
            var item = controls.Nth(i);
            if (!await item.IsVisibleAsync()) continue;
            var desc = await DescribeDateControlAsync(item);
            if (HasAny(desc, "روز", "day")) dayControl = item;
            else if (HasAny(desc, "ماه", "month")) monthControl = item;
            else if (HasAny(desc, "سال", "year")) yearControl = item;

            var tag = await item.EvaluateAsync<string>("el => el.tagName.toLowerCase()");
            if (tag == "select")
                selects.Add((item, await item.Locator("option").CountAsync()));
            if (tag == "input")
            {
                var type = (await item.GetAttributeAsync("type") ?? "text").ToLowerInvariant();
                if (type == "date")
                {
                    var iso = $"{year}-{month.PadLeft(2, '0')}-{day.PadLeft(2, '0')}";
                    await item.FillAsync(iso);
                    return "تاریخ تولد در کادر تاریخ وارد شد.";
                }
            }
        }

        // If this page uses native select elements without labels, use option-list lengths
        // as hints; never guess if the page structure is ambiguous.
        if (dayControl is null || monthControl is null || yearControl is null)
        {
            var daySelect = selects.FirstOrDefault(x => x.OptionCount is >= 27 and <= 33).Locator;
            var monthSelect = selects.FirstOrDefault(x => x.OptionCount is >= 10 and <= 13).Locator;
            var yearSelect = selects.FirstOrDefault(x => x.OptionCount > 33).Locator;
            dayControl ??= daySelect;
            monthControl ??= monthSelect;
            yearControl ??= yearSelect;
        }

        if (dayControl is null || monthControl is null || yearControl is null)
            return "شناسه‌ها وارد شدند؛ کنترل‌های تاریخ قابل شناسایی خودکار نبودند و تاریخ را خودتان در Chrome تکمیل کنید.";

        var dateOk = await SetControlValueAsync(dayControl, day) &&
                     await SetControlValueAsync(monthControl, month) &&
                     await SetControlValueAsync(yearControl, year);
        return dateOk
            ? "تاریخ تولد نیز وارد شد."
            : "شناسه‌ها وارد شدند، اما یکی از گزینه‌های تاریخ پیدا نشد؛ تاریخ را دستی بررسی و تکمیل کنید.";
    }

    private static async Task<bool> SetControlValueAsync(ILocator control, string value)
    {
        var tag = await control.EvaluateAsync<string>("el => el.tagName.toLowerCase()");
        if (tag == "select")
        {
            var options = control.Locator("option");
            var count = await options.CountAsync();
            for (var i = 0; i < count; i++)
            {
                var option = options.Nth(i);
                var label = await option.InnerTextAsync();
                var optionValue = await option.GetAttributeAsync("value") ?? "";
                if (NormalizeNumber(label) == NormalizeNumber(value) || NormalizeNumber(optionValue) == NormalizeNumber(value))
                {
                    if (!string.IsNullOrEmpty(optionValue))
                        await control.SelectOptionAsync(optionValue);
                    else
                        await control.SelectOptionAsync(new SelectOptionValue { Label = label.Trim() });
                    return true;
                }
            }
            return false;
        }
        await control.FillAsync(value);
        return true;
    }

    private static bool TryParseDate(string value, out string day, out string month, out string year)
    {
        var normalized = NormalizeNumber(value.Trim());
        var match = Regex.Match(normalized, @"^(\d{1,4})\s*[/\-.]\s*(\d{1,2})\s*[/\-.]\s*(\d{1,4})$");
        if (!match.Success)
        {
            day = month = year = "";
            return false;
        }
        day = match.Groups[1].Value;
        month = match.Groups[2].Value;
        year = match.Groups[3].Value;
        // The form asks day/month/year. A 4-digit first part means the input was year/month/day.
        if (day.Length == 4) (day, year) = (year, day);
        return int.TryParse(day, out var d) && d is >= 1 and <= 31 &&
               int.TryParse(month, out var m) && m is >= 1 and <= 12 &&
               int.TryParse(year, out var y) && y >= 1000;
    }

    private static string ToAsciiDigits(string value)
        => new string(value.Select(c => c switch
        {
            '۰' => '0', '۱' => '1', '۲' => '2', '۳' => '3', '۴' => '4',
            '۵' => '5', '۶' => '6', '۷' => '7', '۸' => '8', '۹' => '9',
            '٠' => '0', '١' => '1', '٢' => '2', '٣' => '3', '٤' => '4',
            '٥' => '5', '٦' => '6', '٧' => '7', '٨' => '8', '٩' => '9',
            _ => c
        }).ToArray());

    private static string NormalizeNumber(string value)
    {
        var chars = value.Trim().Select(c => c switch
        {
            '۰' => '0', '۱' => '1', '۲' => '2', '۳' => '3', '۴' => '4',
            '۵' => '5', '۶' => '6', '۷' => '7', '۸' => '8', '۹' => '9',
            '٠' => '0', '١' => '1', '٢' => '2', '٣' => '3', '٤' => '4',
            '٥' => '5', '٦' => '6', '٧' => '7', '٨' => '8', '٩' => '9',
            _ => c
        }).ToArray();
        var result = new string(chars);
        return result.TrimStart('0') is { Length: > 0 } trimmed ? trimmed : "0";
    }

    private static string NormalizeForMatch(string value)
    {
        var chars = value.Normalize(System.Text.NormalizationForm.FormKC)
            .Where(c => !char.IsWhiteSpace(c) && c is not '\u200c' and not '\u200f' and not '\u0640')
            .Select(c => c switch { 'ي' => 'ی', 'ك' => 'ک', _ => c }).ToArray();
        return new string(chars);
    }

    private static bool HasAny(string source, params string[] tokens)
        => tokens.Any(t => source.Contains(t, StringComparison.OrdinalIgnoreCase));

    public async ValueTask DisposeAsync()
    {
        if (_context is not null)
        {
            try { await _context.ClearCookiesAsync(); } catch { }
            try { await _context.CloseAsync(); } catch { /* already closed */ }
            _context = null;
            _page = null;
        }
        _playwright?.Dispose();
        _playwright = null;
        try
        {
            if (Directory.Exists(_profileDirectory))
                Directory.Delete(_profileDirectory, recursive: true);
        }
        catch { /* cleanup is best effort during application shutdown */ }
    }

    private sealed record InputCandidate(ILocator Locator, string Type);
}
