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
        if (!url.AbsolutePath.Contains("/register", StringComparison.OrdinalIgnoreCase))
            await NavigateAsync(RegisterUrl);

        // Manual and queue workflows must pass through the same explicit foreigners-choice
        // confirmation before the identity fields can be filled.
        await OpenRegistrationForQueueAsync(CancellationToken.None);
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
        if (!await IsForeignersOptionSelectedAsync())
            throw new InvalidOperationException("پیش از ورود اطلاعات، انتخاب گزینه «اتباع» در صفحه تأیید نشد؛ برای جلوگیری از ثبت اشتباه، اطلاعات وارد نشد.");

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

        // Identify the two identity fields independently. The portal sometimes renders
        // labels as sibling elements (not associated HTML <label>s), and the form may also
        // contain date controls; therefore a global "exactly two inputs" fallback is unsafe.
        var editable = await GetEditableInputsAsync();
        var usedFields = new HashSet<int>();
        var firstMatch = await FindIdentityInputAsync(editable, usedFields,
            new[] { "shenasaeiCode", "shenasaei-code", "passportCode", "passport-code", "شناسه یکتا", "شناسه", "یکتا", "کد شناسایی", "شناسایی", "گذرنامه", "فراگیر", "کد اتباع", "passport", "unique", "identity" });
        if (firstMatch.Locator is not null) usedFields.Add(firstMatch.Index);
        var secondMatch = await FindIdentityInputAsync(editable, usedFields,
            new[] { "fidaCode", "fida-code", "کد فیدا", "فیدا", "fida" });

        ILocator? first = firstMatch.Locator;
        ILocator? second = secondMatch.Locator;
        if (first is null || second is null)
        {
            // Positional fallback is allowed only when exactly two editable fields exist.
            if (editable.Count == 2)
            {
                first = editable[0].Locator;
                second = editable[1].Locator;
            }
            else
            {
                throw new InvalidOperationException(await BuildRegistrationDiagnosticAsync(
                    "کادرهای شناسه یکتا و کد فیدا با اطمینان از روی مشخصات فرم شناسایی نشدند؛ اطلاعات وارد نشد تا در فیلد اشتباه قرار نگیرد."));
            }
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
        if (NormalizeDigitsExact(actualFirst) != NormalizeDigitsExact(expectedFirst) ||
            NormalizeDigitsExact(actualSecond) != NormalizeDigitsExact(expectedSecond))
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
        await EnterOtpForAutomaticVerificationAsync(otp.Trim());
        progress.Report("کد پیامکی در فرم سامانه وارد شد؛ در حال بررسی نتیجه ورود...");
        await WaitForAuthenticatedPortalAsync(TimeSpan.FromSeconds(90), cancellationToken);

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

    private async Task EnterOtpForAutomaticVerificationAsync(string otp)
    {
        RequirePage();
        var code = ToAsciiDigits(otp.Trim());
        if (code.Length < 4 || code.Length > 8)
            throw new InvalidOperationException("کد پیامکی باید بین ۴ تا ۸ رقم باشد.");

        async Task<bool> HasReachedAuthenticatedPageAsync()
            => await TryAdoptAuthenticatedPortalPageAsync();

        // Do not use a locator that can silently change from OTP boxes to unrelated
        // one-character controls after the portal navigates. The official portal validates
        // automatically on the last digit, and may navigate to /register during this loop.
        var splitBoxes = _page!.Locator("input:visible[maxlength='1']");
        var splitCount = await splitBoxes.CountAsync();

        if (splitCount >= code.Length)
        {
            for (var i = 0; i < code.Length; i++)
            {
                if (await HasReachedAuthenticatedPageAsync())
                    return;

                var currentCount = await splitBoxes.CountAsync();
                if (currentCount <= i)
                {
                    if (await HasReachedAuthenticatedPageAsync())
                        return;

                    throw new InvalidOperationException(
                        $"کادر شماره {i + 1} کد پیامکی در صفحه ورود موجود نیست. " +
                        "احتمالاً فرم سامانه تغییر کرده یا صفحه پیش از تکمیل کد جابه‌جا شده است؛ کد در فیلد دیگری وارد نشد.");
                }

                var box = splitBoxes.Nth(i);
                try
                {
                    if (!await box.IsVisibleAsync())
                    {
                        if (await HasReachedAuthenticatedPageAsync())
                            return;

                        throw new InvalidOperationException($"کادر شماره {i + 1} کد پیامکی قابل مشاهده نیست.");
                    }

                    await box.ClickAsync(new LocatorClickOptions { Timeout = 2500 });
                    await box.FillAsync(string.Empty, new LocatorFillOptions { Timeout = 2500 });
                    await box.PressSequentiallyAsync(
                        code[i].ToString(),
                        new LocatorPressSequentiallyOptions { Timeout = 2500 });
                }
                catch (PlaywrightException ex)
                {
                    if (await HasReachedAuthenticatedPageAsync())
                        return;

                    throw new InvalidOperationException(
                        $"ورود رقم {i + 1} از کد پیامکی انجام نشد. صفحه Chrome را بررسی کنید. جزئیات: {ex.Message}", ex);
                }

                // If the last digit triggered automatic verification and navigation,
                // stop touching the old OTP locators immediately.
                if (await HasReachedAuthenticatedPageAsync())
                    return;
            }

            if (await HasReachedAuthenticatedPageAsync())
                return;

            var entered = new System.Text.StringBuilder();
            try
            {
                for (var i = 0; i < code.Length; i++)
                {
                    if (await HasReachedAuthenticatedPageAsync())
                        return;

                    entered.Append(ToAsciiDigits(await splitBoxes.Nth(i).InputValueAsync()));
                }
            }
            catch (PlaywrightException ex)
            {
                if (await HasReachedAuthenticatedPageAsync())
                    return;

                throw new InvalidOperationException("خواندن مقدار کد پیامکی برای کنترل نهایی ممکن نشد.", ex);
            }

            if (entered.ToString() != code)
                throw new InvalidOperationException("رقم‌های کد پیامکی به‌درستی در کادرهای سامانه وارد نشدند.");
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

            try
            {
                await otpInput.ClickAsync(new LocatorClickOptions { Timeout = 2500 });
                await otpInput.FillAsync(string.Empty, new LocatorFillOptions { Timeout = 2500 });
                await otpInput.PressSequentiallyAsync(code,
                    new LocatorPressSequentiallyOptions { Timeout = 5000 });
            }
            catch (PlaywrightException ex)
            {
                if (await HasReachedAuthenticatedPageAsync())
                    return;

                throw new InvalidOperationException("ورود کد پیامکی انجام نشد؛ صفحه Chrome را بررسی کنید.", ex);
            }

            if (await HasReachedAuthenticatedPageAsync())
                return;

            var actual = ToAsciiDigits(await otpInput.InputValueAsync());
            if (actual != code)
                throw new InvalidOperationException("کد پیامکی در کادر سامانه درست ثبت نشد؛ کد را در Chrome بررسی کنید.");
        }

        // No extra confirm click: the portal validates the OTP from its own input handler.
        await Task.Delay(300);
        if (!await HasReachedAuthenticatedPageAsync())
            await _page.BringToFrontAsync();
    }

    private async Task<bool> TryAdoptAuthenticatedPortalPageAsync()
    {
        if (_context is null)
            return false;

        // SSO sometimes completes authentication by opening the registration page in a
        // second Chrome tab. Keep the automation attached to the newest authenticated tab,
        // not permanently to the original /login tab.
        foreach (var candidate in _context.Pages.Reverse())
        {
            if (candidate.IsClosed ||
                !Uri.TryCreate(candidate.Url, UriKind.Absolute, out var uri) ||
                !uri.Host.EndsWith("my.gov.ir", StringComparison.OrdinalIgnoreCase))
                continue;

            var path = uri.AbsolutePath;
            var isDashboard = path.Contains("/dashboard", StringComparison.OrdinalIgnoreCase);
            var isAccount = path.Contains("/account", StringComparison.OrdinalIgnoreCase);
            var isLoginRoute = path.Contains("/login", StringComparison.OrdinalIgnoreCase);

            if (isDashboard || isAccount)
            {
                _page = candidate;
                try { await _page.BringToFrontAsync(); } catch { }
                return true;
            }

            string body;
            try
            {
                body = await candidate.Locator("body").InnerTextAsync(
                    new LocatorInnerTextOptions { Timeout = 1800 });
            }
            catch
            {
                continue;
            }

            var normalized = NormalizeForMatch(body);
            var hasRegistrationShell =
                normalized.Contains(NormalizeForMatch("اتباع"), StringComparison.Ordinal) &&
                normalized.Contains(NormalizeForMatch("ایرانی"), StringComparison.Ordinal) &&
                normalized.Contains(NormalizeForMatch("تولد"), StringComparison.Ordinal) &&
                (normalized.Contains(NormalizeForMatch("کد فیدا"), StringComparison.Ordinal) ||
                 normalized.Contains(NormalizeForMatch("کدملی"), StringComparison.Ordinal));

            var hasAuthenticatedIdentityForm =
                normalized.Contains(NormalizeForMatch("کد فیدا"), StringComparison.Ordinal) &&
                (normalized.Contains(NormalizeForMatch("شناسه"), StringComparison.Ordinal) ||
                 normalized.Contains(NormalizeForMatch("گذرنامه"), StringComparison.Ordinal) ||
                 normalized.Contains(NormalizeForMatch("فراگیر"), StringComparison.Ordinal));

            if (hasRegistrationShell ||
                (!isLoginRoute && hasAuthenticatedIdentityForm))
            {
                _page = candidate;
                try { await _page.BringToFrontAsync(); } catch { }
                return true;
            }
        }

        return false;
    }

    private async Task WaitForAuthenticatedPortalAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_context is null || _context.Pages.All(page => page.IsClosed))
                throw new InvalidOperationException("تمام پنجره‌های Chrome بسته شدند؛ ورود متوقف شد.");

            if (await TryAdoptAuthenticatedPortalPageAsync())
                return;

            if (_page is null || _page.IsClosed)
                _page = _context.Pages.LastOrDefault(page => !page.IsClosed);

            var body = await ReadBodyTextSafelyAsync();
            var securityCodeRejected = body.Contains("کد امنیتی", StringComparison.OrdinalIgnoreCase) &&
                                       ContainsAny(body, "اشتباه", "صحیح نیست", "نامعتبر", "نادرست");
            var oneTimeCodeRejected = ContainsAny(body, "کد تأیید", "کد تایید", "کد پیامکی", "کد یکبار مصرف") &&
                                      ContainsAny(body, "اشتباه", "صحیح نیست", "نامعتبر", "نادرست");
            if (securityCodeRejected || oneTimeCodeRejected)
                throw new InvalidOperationException("سامانه کد امنیتی یا کد پیامکی را نپذیرفت. کد و پیام خطا را بررسی و پرونده را دوباره اجرا کنید.");
            var normalizedBody = NormalizeForMatch(body);
            var hasRegistrationShell =
                normalizedBody.Contains(NormalizeForMatch("اتباع"), StringComparison.Ordinal) &&
                normalizedBody.Contains(NormalizeForMatch("ایرانی"), StringComparison.Ordinal) &&
                normalizedBody.Contains(NormalizeForMatch("تولد"), StringComparison.Ordinal) &&
                (normalizedBody.Contains(NormalizeForMatch("کد فیدا"), StringComparison.Ordinal) ||
                 normalizedBody.Contains(NormalizeForMatch("کدملی"), StringComparison.Ordinal));
            if (hasRegistrationShell)
                return;

            await Task.Delay(700, cancellationToken);
        }

        var tabDetails = _context is null
            ? "هیچ نشست فعالی وجود ندارد."
            : string.Join(" | ", _context.Pages.Where(page => !page.IsClosed).Select(page => page.Url));
        var lastBody = await ReadBodyTextSafelyAsync();
        throw new TimeoutException(
            "پس از وارد کردن کد پیامکی، ورود در ۹۰ ثانیه تأیید نشد. اگر صفحه ثبت‌نام در تب دیگری باز شده باشد، برنامه آن را بررسی کرده است. " +
            "کد یا پیام خطای سامانه را در Chrome بررسی کنید.\nنشانی تب‌ها: " + tabDetails +
            "\nمتن صفحه: " + Truncate(lastBody, 450));
    }

    private async Task OpenRegistrationForQueueAsync(CancellationToken cancellationToken)
    {
        RequirePage();
        if (!new Uri(_page!.Url).AbsolutePath.Contains("/register", StringComparison.OrdinalIgnoreCase))
            await NavigateAsync(RegisterUrl);

        try { await _page!.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions { Timeout = 20000 }); }
        catch { /* The portal is an SPA and may not emit a full load event. */ }

        // IMPORTANT: identity inputs can already exist while the "اتباع" choice is still
        // unselected. Never treat input presence as proof that the foreigners option was clicked.
        var identityFormReadyBeforeSelection = await IsIdentityFormReadyAsync();
        var selectionConfirmed = await IsForeignersOptionSelectedAsync();

        var renderWait = DateTime.UtcNow;
        while (!selectionConfirmed && DateTime.UtcNow - renderWait < TimeSpan.FromSeconds(20))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsForeignersOptionVisibleAsync()) break;
            selectionConfirmed = await IsForeignersOptionSelectedAsync();
            if (!selectionConfirmed) await Task.Delay(300, cancellationToken);
        }

        // Some portal versions expose "اتباع" as an option in a native HTML select.
        if (!selectionConfirmed)
        {
            foreach (var frame in _page!.Frames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var selects = frame.Locator("select:visible");
                var selectCount = await selects.CountAsync();
                for (var i = 0; i < selectCount; i++)
                {
                    var select = selects.Nth(i);
                    var options = select.Locator("option");
                    var optionCount = await options.CountAsync();
                    for (var j = 0; j < optionCount; j++)
                    {
                        var option = options.Nth(j);
                        var label = (await option.InnerTextAsync()).Trim();
                        var value = await option.GetAttributeAsync("value") ?? "";
                        if (!NormalizeForMatch(label).Contains(NormalizeForMatch("اتباع"), StringComparison.Ordinal))
                            continue;

                        if (!string.IsNullOrWhiteSpace(value))
                            await select.SelectOptionAsync(value, new LocatorSelectOptionOptions { Timeout = 5000 });
                        else
                            await select.SelectOptionAsync(new[] { new SelectOptionValue { Label = label } },
                                new LocatorSelectOptionOptions { Timeout = 5000 });

                        selectionConfirmed = await WaitForForeignersSelectionAsync(TimeSpan.FromSeconds(8), cancellationToken);
                        if (selectionConfirmed) break;
                    }
                    if (selectionConfirmed) break;
                }
                if (selectionConfirmed) break;
            }
        }

        // Other portal versions render "اتباع" as a radio, tab, button, label, or nested card.
        // Mark the actionable ancestor in JavaScript, then use Playwright's real pointer click.
        if (!selectionConfirmed)
        {
            foreach (var frame in _page!.Frames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var result = await frame.EvaluateAsync<string>("""
                        () => {
                          const norm = s => (s || '').normalize('NFKC')
                            .replace(/[\s\u200c\u200f\u0640]/g, '')
                            .replace(/[ي]/g, 'ی').replace(/[ك]/g, 'ک');
                          const visible = el => {
                            if (!el || !el.getClientRects().length) return false;
                            const style = getComputedStyle(el);
                            return style.visibility !== 'hidden' && style.display !== 'none' &&
                              style.opacity !== '0' && !el.closest('[aria-hidden=true]');
                          };
                          const describes = el => norm([
                            el.innerText, el.textContent, el.getAttribute('aria-label'),
                            el.getAttribute('title'), el.value
                          ].filter(Boolean).join(' '));
                          const hasSelectedClass = className => {
                            const tokens = (className || '').toString()
                              .replace(/([a-z0-9])([A-Z])/g, '$1-$2')
                              .toLowerCase().split(/\s+/);
                            return tokens.some(token => {
                              if (/(^|[-_])(inactive|unselected|unchecked|not-active|not-selected)([-_]|$)/.test(token))
                                return false;
                              return /(^|[-_])(active|selected|checked)([-_]|$)/.test(token);
                            });
                          };
                          const selected = el => {
                            const actionable = el.closest('button,[role=tab],[role=button],[role=radio],a,label,[tabindex],li') || el;
                            const nodes = [...new Set([el, actionable])].filter(Boolean);
                            for (const node of nodes) {
                              const cls = node.className?.toString() || '';
                              if (node.getAttribute('aria-selected') === 'true' ||
                                  node.getAttribute('aria-checked') === 'true' ||
                                  node.getAttribute('aria-pressed') === 'true' ||
                                  node.getAttribute('aria-current') === 'true' ||
                                  node.getAttribute('data-selected') === 'true' ||
                                  hasSelectedClass(cls))
                                return true;
                              const radio = node.matches?.('input[type=radio]') ? node :
                                (node.querySelector?.('input[type=radio]') ||
                                 (node.matches?.('label[for]') ? document.getElementById(node.getAttribute('for')) : null));
                              if (radio?.checked) return true;
                            }
                            const radio = el.matches?.('input[type=radio]') ? el :
                              (el.closest('label')?.querySelector('input[type=radio]') ||
                               (el.matches?.('label[for]') ? document.getElementById(el.getAttribute('for')) : null));
                            return radio?.checked === true;
                          };

                          const select = [...document.querySelectorAll('select')]
                            .find(s => visible(s) &&
                              [...s.selectedOptions].some(o => norm(o.label || o.textContent).includes('اتباع')));
                          if (select) return 'selected';

                          const radios = [...document.querySelectorAll('input[type=radio]')];
                          for (const radio of radios) {
                            const label = [
                              ...(radio.labels ? [...radio.labels] : []),
                              radio.closest('label'),
                              radio.parentElement
                            ].filter(Boolean).map(x => x.innerText || x.textContent || '').join(' ');
                            if (!norm(label).includes('اتباع')) continue;
                            if (radio.checked) return 'selected';
                            const labelTarget = radio.closest('label') ||
                              (radio.id ? document.querySelector('label[for="' + CSS.escape(radio.id) + '"]') : null);
                            if (labelTarget && visible(labelTarget)) {
                              labelTarget.setAttribute('data-dolatman-foreigners-choice', 'radio');
                              return 'click-ready';
                            }
                          }

                          const selector = 'input[type=radio],[role=tab],button,[role=button],[role=radio],a,label,[tabindex],li,div,span';
                          const candidates = [...document.querySelectorAll(selector)]
                            .filter(el => visible(el))
                            .map(el => ({ el, text: describes(el) }))
                            .filter(x => x.text.includes('اتباع') && !x.text.includes('ایرانی') && x.text.length <= 30)
                            .sort((a,b) => {
                              const pa = a.el.matches('button,[role=tab],[role=button],[role=radio],a,label,[tabindex]') ? 0 : 1;
                              const pb = b.el.matches('button,[role=tab],[role=button],[role=radio],a,label,[tabindex]') ? 0 : 1;
                              return pa - pb || a.text.length - b.text.length;
                            });

                          for (const candidate of candidates) {
                            const chosen = candidate.el;
                            if (selected(chosen)) return 'selected';
                            let actionable = chosen.closest('button,[role=tab],[role=button],[role=radio],a,label,[tabindex],li');
                            if (!actionable) {
                              for (let node = chosen, depth = 0; node && depth < 6; node = node.parentElement, depth++) {
                                const style = getComputedStyle(node);
                                const cls = node.className?.toString() || '';
                                if (node.onclick || style.cursor === 'pointer' ||
                                    /tab|card|option|choice|select/i.test(cls)) {
                                  actionable = node;
                                  break;
                                }
                              }
                            }
                            actionable ||= chosen;
                            if (!visible(actionable) || actionable.matches(':disabled,[aria-disabled="true"]'))
                              continue;
                            actionable.setAttribute('data-dolatman-foreigners-choice', 'click');
                            return 'click-ready';
                          }
                          return 'not-found';
                        }
                        """);

                    if (result == "selected")
                    {
                        selectionConfirmed = true;
                        break;
                    }

                    if (result == "click-ready")
                    {
                        var target = frame.Locator("[data-dolatman-foreigners-choice]");
                        if (await target.CountAsync() > 0 && await target.First.IsVisibleAsync())
                        {
                            await target.First.ClickAsync(new LocatorClickOptions { Timeout = 10000, Force = true });
                            selectionConfirmed = await WaitForForeignersSelectionAsync(
                                TimeSpan.FromSeconds(8), cancellationToken);

                            // On portal builds without aria/class selection metadata, a successful
                            // click that reveals the identity controls is the second confirmation.
                            if (!selectionConfirmed && !identityFormReadyBeforeSelection)
                                selectionConfirmed = await IsIdentityFormReadyAsync();

                            if (selectionConfirmed) break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Continue searching other frames; the diagnostic below reports visible choices.
                }
            }
        }

        // The portal's current segmented control can be rendered as nested DIV/SPAN
        // elements without role, tabindex, or a native button. In that layout the previous
        // ancestor heuristic can find the text but click a non-interactive child. Try the
        // exact visible text and then its text-only ancestors using real Playwright clicks.
        if (!selectionConfirmed)
        {
            foreach (var frame in _page!.Frames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var exactText = frame.GetByText("اتباع");
                var matchCount = Math.Min(await exactText.CountAsync(), 20);

                for (var i = 0; i < matchCount && !selectionConfirmed; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ILocator candidate = exactText.Nth(i);

                    for (var depth = 0; depth < 5 && !selectionConfirmed; depth++)
                    {
                        if (await IsForeignersOptionSelectedAsync())
                        {
                            selectionConfirmed = true;
                            break;
                        }

                        try
                        {
                            if (await candidate.IsVisibleAsync())
                            {
                                var candidateText = NormalizeForMatch(await candidate.InnerTextAsync());
                                if (candidateText == NormalizeForMatch("اتباع"))
                                {
                                    await candidate.ClickAsync(new LocatorClickOptions { Timeout = 2500, Force = true });
                                    selectionConfirmed = await WaitForForeignersSelectionAsync(
                                        TimeSpan.FromSeconds(3), cancellationToken);

                                    // Some portal builds do not expose selected-state ARIA/classes;
                                    // in that case the two dedicated foreigner identity inputs must
                                    // appear as a consequence of this click.
                                    if (!selectionConfirmed && !identityFormReadyBeforeSelection)
                                        selectionConfirmed = await IsIdentityFormReadyAsync();
                                }
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (PlaywrightException)
                        {
                            // Try a native click on the exact visible label; this also supports
                            // custom React/Vue tabs rendered as nested non-semantic spans.
                            try
                            {
                                await candidate.EvaluateAsync("el => el.click()");
                                selectionConfirmed = await WaitForForeignersSelectionAsync(
                                    TimeSpan.FromSeconds(2), cancellationToken);
                                if (!selectionConfirmed && !identityFormReadyBeforeSelection)
                                    selectionConfirmed = await IsIdentityFormReadyAsync();
                            }
                            catch (OperationCanceledException) { throw; }
                            catch { /* Try the next exact-text ancestor. */ }
                        }

                        if (!selectionConfirmed)
                        {
                            try { candidate = candidate.Locator("xpath=.."); }
                            catch { break; }
                        }
                    }
                }

                if (selectionConfirmed)
                    break;
            }
        }

        // Do not proceed to typing unless the option itself was confirmed, not merely the form.
        if (!selectionConfirmed)
            throw new InvalidOperationException(await BuildRegistrationDiagnosticAsync(
                "گزینه «اتباع» انتخاب نشد یا انتخاب آن قابل تأیید نبود. اطلاعات متقاضی وارد نشد و هیچ فرمی ارسال نشد."));

        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < TimeSpan.FromSeconds(60))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsIdentityFormReadyAsync()) return;
            await Task.Delay(400, cancellationToken);
        }

        throw new TimeoutException(await BuildRegistrationDiagnosticAsync(
            "گزینه «اتباع» انتخاب شد، اما کادرهای شناسه یکتا و کد فیدا در صفحه ظاهر نشدند."));
    }

    private async Task<bool> IsForeignersOptionVisibleAsync()
    {
        foreach (var frame in _page!.Frames)
        {
            try
            {
                var visible = await frame.EvaluateAsync<bool>("""
                    () => {
                      const norm = s => (s || '').normalize('NFKC')
                        .replace(/[\s\u200c\u200f\u0640]/g, '')
                        .replace(/[ي]/g, 'ی').replace(/[ك]/g, 'ک');
                      const isVisible = el => {
                        if (!el || !el.getClientRects().length) return false;
                        const style = getComputedStyle(el);
                        return style.visibility !== 'hidden' && style.display !== 'none' &&
                          style.opacity !== '0' && !el.closest('[aria-hidden=true]');
                      };
                      for (const select of document.querySelectorAll('select')) {
                        if (isVisible(select) &&
                            [...select.options].some(o => norm(o.label || o.textContent).includes('اتباع')))
                          return true;
                      }
                      const selector = 'input[type=radio],[role=tab],button,[role=button],[role=radio],a,label,[tabindex],li,div,span';
                      return [...document.querySelectorAll(selector)].some(el => {
                        if (!isVisible(el)) return false;
                        const label = norm([
                          el.innerText, el.textContent, el.getAttribute('aria-label'), el.getAttribute('title')
                        ].filter(Boolean).join(' '));
                        return label.includes('اتباع') && !label.includes('ایرانی') && label.length <= 30;
                      });
                    }
                    """);
                if (visible) return true;
            }
            catch { }
        }
        return false;
    }

    private async Task<bool> IsForeignersOptionSelectedAsync()
    {
        foreach (var frame in _page!.Frames)
        {
            try
            {
                var selected = await frame.EvaluateAsync<bool>("""
                    () => {
                      const norm = s => (s || '').normalize('NFKC')
                        .replace(/[\s\u200c\u200f\u0640]/g, '')
                        .replace(/[ي]/g, 'ی').replace(/[ك]/g, 'ک');
                      const visible = el => {
                        if (!el || !el.getClientRects().length) return false;
                        const style = getComputedStyle(el);
                        return style.visibility !== 'hidden' && style.display !== 'none' &&
                          style.opacity !== '0' && !el.closest('[aria-hidden=true]');
                      };
                      const hasSelectedClass = className => {
                        const tokens = (className || '').toString()
                          .replace(/([a-z0-9])([A-Z])/g, '$1-$2')
                          .toLowerCase().split(/\s+/);
                        return tokens.some(token => {
                          if (/(^|[-_])(inactive|unselected|unchecked|not-active|not-selected)([-_]|$)/.test(token))
                            return false;
                          return /(^|[-_])(active|selected|checked)([-_]|$)/.test(token);
                        });
                      };
                      const selectedState = el => {
                        const actionable = el.closest('button,[role=tab],[role=button],[role=radio],a,label,[tabindex],li') || el;
                        const nodes = [...new Set([el, actionable])].filter(Boolean);
                        for (const node of nodes) {
                          const cls = node.className?.toString() || '';
                          if (node.getAttribute('aria-selected') === 'true' ||
                              node.getAttribute('aria-checked') === 'true' ||
                              node.getAttribute('aria-pressed') === 'true' ||
                              node.getAttribute('aria-current') === 'true' ||
                              node.getAttribute('data-selected') === 'true' ||
                              hasSelectedClass(cls))
                            return true;
                          const radio = node.matches?.('input[type=radio]') ? node :
                            (node.querySelector?.('input[type=radio]') ||
                             (node.matches?.('label[for]') ? document.getElementById(node.getAttribute('for')) : null));
                          if (radio?.checked) return true;
                        }
                        const radio = el.matches?.('input[type=radio]') ? el :
                          (el.closest('label')?.querySelector('input[type=radio]') ||
                           (el.matches?.('label[for]') ? document.getElementById(el.getAttribute('for')) : null));
                        return radio?.checked === true;
                      };
                      for (const select of document.querySelectorAll('select')) {
                        if (visible(select) &&
                            [...select.selectedOptions].some(o => norm(o.label || o.textContent).includes('اتباع')))
                          return true;
                      }
                      const selector = 'input[type=radio],[role=tab],button,[role=button],[role=radio],a,label,[tabindex],li,div,span';
                      return [...document.querySelectorAll(selector)].some(el => {
                        if (!visible(el)) return false;
                        const label = norm([
                          el.innerText, el.textContent, el.getAttribute('aria-label'), el.getAttribute('title')
                        ].filter(Boolean).join(' '));
                        return label.includes('اتباع') && !label.includes('ایرانی') && label.length <= 30 && selectedState(el);
                      });
                    }
                    """);
                if (selected) return true;
            }
            catch { }
        }
        return false;
    }

    private async Task<bool> WaitForForeignersSelectionAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsForeignersOptionSelectedAsync()) return true;
            await Task.Delay(250, cancellationToken);
        }
        return false;
    }

    private async Task<bool> IsIdentityFormReadyAsync()
    {
        var editable = await GetEditableInputsAsync();
        var used = new HashSet<int>();
        var first = await FindIdentityInputAsync(editable, used,
            new[] { "shenasaeiCode", "passportCode", "شناسه یکتا", "شناسه", "یکتا", "گذرنامه", "فراگیر", "passport", "unique" });
        if (first.Locator is not null)
        {
            used.Add(first.Index);
            var second = await FindIdentityInputAsync(editable, used,
                new[] { "fidaCode", "کد فیدا", "فیدا", "fida" });
            if (second.Locator is not null)
                return true;
        }

        // On the live SSO registration page the visible captions can be rendered as
        // decorative sibling spans, not associated <label> elements. In that layout
        // Playwright can see exactly two editable inputs but metadata matching may miss
        // the FIDA caption. Accept that positional layout only when the page itself
        // explicitly contains both foreigner identity captions and the «اتباع» context.
        if (editable.Count != 2)
            return false;

        var body = NormalizeForMatch(await ReadBodyTextSafelyAsync());
        var hasForeignerChoice = body.Contains(NormalizeForMatch("اتباع"), StringComparison.Ordinal);
        var hasUniqueCaption =
            body.Contains(NormalizeForMatch("گذرنامه"), StringComparison.Ordinal) ||
            body.Contains(NormalizeForMatch("فراگیر"), StringComparison.Ordinal) ||
            body.Contains(NormalizeForMatch("شناسه یکتا"), StringComparison.Ordinal);
        var hasFidaCaption = body.Contains(NormalizeForMatch("کد فیدا"), StringComparison.Ordinal);

        return hasForeignerChoice && hasUniqueCaption && hasFidaCaption;
    }

    private async Task<string> BuildRegistrationDiagnosticAsync(string reason)
    {
        var body = await ReadBodyTextSafelyAsync();
        var inputs = await GetEditableInputsAsync();
        var details = new List<string>();
        foreach (var item in inputs.Take(8))
        {
            var desc = await DescribeIdentityFieldAsync(item.Locator);
            details.Add(string.IsNullOrWhiteSpace(desc) ? $"input({item.Type})" : desc.Replace('\u001f', '/').Trim());
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
        var tokenValues = tokens.Select(x => NormalizeForMatch(x).ToLowerInvariant())
            .Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();

        // Search each frame in one DOM snapshot instead of counting a locator and then
        // indexing it with Nth(i). The portal re-renders buttons during login and that
        // made the old indexed locator wait 30 seconds for an element that had moved.
        foreach (var frame in _page!.Frames)
        {
            try
            {
                var result = await frame.EvaluateAsync<string>("""
                    tokens => {
                      const norm = s => (s || '').normalize('NFKC')
                        .replace(/[\s\u200c\u200f\u0640]/g, '')
                        .replace(/[ي]/g, 'ی').replace(/[ك]/g, 'ک').toLowerCase();
                      document.querySelectorAll('[data-dolatman-click-target]')
                        .forEach(el => el.removeAttribute('data-dolatman-click-target'));
                      const items = [...document.querySelectorAll(
                        'button,input[type=submit],[role=button],a,[role=tab],[role=radio],label,[tabindex]'
                      )].filter(el => {
                        const style = getComputedStyle(el);
                        return el.getClientRects().length &&
                          style.display !== 'none' && style.visibility !== 'hidden' &&
                          style.opacity !== '0' && !el.matches(':disabled,[aria-disabled="true"]') &&
                          !el.closest('[aria-hidden=true]');
                      }).map(el => {
                        const text = el.matches('input') ?
                          (el.value || el.getAttribute('aria-label') || el.title || '') :
                          (el.innerText || el.textContent || el.getAttribute('aria-label') || el.title || '');
                        return { el, text: norm(text) };
                      }).filter(x => tokens.some(t => x.text.includes(norm(t))));
                      items.sort((a,b) => {
                        const ae = tokens.some(t => a.text === norm(t)) ? 0 : 1;
                        const be = tokens.some(t => b.text === norm(t)) ? 0 : 1;
                        return ae - be || a.text.length - b.text.length;
                      });
                      if (!items.length) return 'not-found';
                      items[0].el.setAttribute('data-dolatman-click-target','true');
                      return 'found';
                    }
                    """, tokenValues);

                if (result != "found") continue;
                var target = frame.Locator("[data-dolatman-click-target='true']");
                if (await target.CountAsync() > 0 && await target.First.IsVisibleAsync())
                    return target.First;
            }
            catch
            {
                // A frame may detach while the portal navigates; continue with remaining frames.
            }
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

    private async Task<(ILocator? Locator, int Index)> FindIdentityInputAsync(
        List<InputCandidate> candidates, HashSet<int> excluded, string[] tokens)
    {
        var ranked = new List<(ILocator Locator, int Index, int Score)>();
        var normalizedTokens = tokens.Select(NormalizeForMatch).Where(t => t.Length > 0).ToArray();
        for (var i = 0; i < candidates.Count; i++)
        {
            if (excluded.Contains(i)) continue;
            var parts = (await DescribeIdentityFieldAsync(candidates[i].Locator)).Split('\u001f');
            var direct = parts.Length > 0 ? NormalizeForMatch(parts[0]) : "";
            var nearby = parts.Length > 1 ? NormalizeForMatch(parts[1]) : "";
            var directMatch = normalizedTokens.Any(t => direct.Contains(t, StringComparison.Ordinal));
            var nearbyMatch = normalizedTokens.Any(t => nearby.Contains(t, StringComparison.Ordinal));
            var directSpecificity = normalizedTokens.Where(t => direct.Contains(t, StringComparison.Ordinal)).Select(t => t.Length).DefaultIfEmpty(0).Max();
            var nearbySpecificity = normalizedTokens.Where(t => nearby.Contains(t, StringComparison.Ordinal)).Select(t => t.Length).DefaultIfEmpty(0).Max();
            var score = directMatch ? 100 + directSpecificity : nearbyMatch ? 20 + nearbySpecificity : 0;
            if (score > 0) ranked.Add((candidates[i].Locator, i, score));
        }

        if (ranked.Count == 0) return (null, -1);
        var bestScore = ranked.Max(x => x.Score);
        var best = ranked.Where(x => x.Score == bestScore).ToList();
        // Ambiguous labels are safer to report than to guess between two inputs.
        if (best.Count != 1) return (null, -1);
        return (best[0].Locator, best[0].Index);
    }

    private static async Task<string> DescribeIdentityFieldAsync(ILocator locator)
    {
        return await locator.EvaluateAsync<string>("""
            el => {
              const texts = nodes => nodes.map(n => n?.innerText || n?.textContent || '').filter(Boolean).join(' ');
              const direct = [
                el.getAttribute('placeholder'), el.getAttribute('name'), el.id,
                el.getAttribute('aria-label'), el.getAttribute('title'),
                el.getAttribute('data-testid'), el.getAttribute('data-name'), el.getAttribute('data-placeholder'),
                el.getAttribute('aria-describedby'), el.getAttribute('autocomplete'),
                texts(el.labels ? [...el.labels] : []),
                texts((el.getAttribute('aria-labelledby') || '').split(/\s+/).map(id => document.getElementById(id))),
                el.closest('label')?.innerText,
                el.previousElementSibling?.innerText,
                el.nextElementSibling?.innerText
              ].filter(Boolean).join(' ');
              const parentText = el.parentElement?.innerText || '';
              const grandText = el.parentElement?.parentElement?.innerText || '';
              const nearby = [parentText.length <= 220 ? parentText : '',
                              grandText.length <= 160 ? grandText : ''].filter(Boolean).join(' ');
              return direct + '\u001f' + nearby;
            }
            """);
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
            return "تاریخ تولد خالی یا با قالب نامعتبر است؛ آن را به شکل روز/ماه/سال در Chrome دستی تکمیل کنید. فرم تا تکمیل تاریخ ارسال نمی‌شود.";

        var dayControl = await FindDateControlAsync("day");
        var monthControl = await FindDateControlAsync("month");
        var yearControl = await FindDateControlAsync("year");

        if (dayControl.Locator is null || monthControl.Locator is null || yearControl.Locator is null)
            throw new InvalidOperationException(await BuildRegistrationDiagnosticAsync(
                "سه فهرست کشویی روز، ماه و سال تولد به‌طور مطمئن شناسایی نشدند؛ فرم ثبت نشد."));

        var monthName = GetPersianMonthName(month);
        var dayOk = await SelectDateControlAsync(dayControl.Frame!, dayControl.Locator, day, day);
        var monthOk = await SelectDateControlAsync(monthControl.Frame!, monthControl.Locator, month, monthName);
        var yearOk = await SelectDateControlAsync(yearControl.Frame!, yearControl.Locator, year, year);

        if (!dayOk || !monthOk || !yearOk)
        {
            var failed = string.Join("، ", new[]
            {
                dayOk ? null : "روز",
                monthOk ? null : "ماه",
                yearOk ? null : "سال"
            }.Where(x => x is not null));
            throw new InvalidOperationException(
                $"انتخاب تاریخ تولد در فهرست کشویی «{failed}» تأیید نشد؛ برای جلوگیری از ثبت تاریخ اشتباه، فرم ارسال نشد.");
        }

        return $"تاریخ تولد {day}/{month}/{year} در فهرست‌های کشویی انتخاب و کنترل شد.";
    }

    private async Task<(IFrame? Frame, ILocator? Locator)> FindDateControlAsync(string field)
    {
        foreach (var frame in _page!.Frames)
        {
            try
            {
                var found = await frame.EvaluateAsync<bool>("(field) => {"
                    + "const aliases={day:['روز','day'],month:['ماه','month'],year:['سال','year']};"
                    + "const other={day:['ماه','سال'],month:['روز','سال'],year:['روز','ماه']};"
                    + "const norm=s=>(s||'').normalize('NFKC').replace(/[\\s\\u200c\\u200f\\u0640]/g,'').replace(/[ي]/g,'ی').replace(/[ك]/g,'ک').toLowerCase();"
                    + "const visible=el=>{if(!el||!el.getClientRects().length)return false;const st=getComputedStyle(el);return st.display!=='none'&&st.visibility!=='hidden'&&st.opacity!=='0'&&!el.closest('[aria-hidden=true]');};"
                    + "const selector='input,select,button,[role=combobox],[role=button],[aria-haspopup],[aria-expanded],[tabindex],[class*=select],[class*=Select],[class*=dropdown],[class*=Dropdown],div,span';"
                    + "const candidates=[...document.querySelectorAll(selector)].filter(visible).map(el=>{"
                    + "const own=(el.innerText||el.textContent||'').trim();"
                    + "const attrs=[el.getAttribute('placeholder'),el.getAttribute('name'),el.id,el.getAttribute('aria-label'),el.getAttribute('title'),el.getAttribute('data-testid'),el.getAttribute('class')].filter(Boolean).join(' ');"
                    + "const siblings=[el.previousElementSibling?.innerText,el.nextElementSibling?.innerText].filter(Boolean).join(' ');"
                    + "const direct=norm(attrs+' '+own);const near=norm(siblings);"
                    + "let score=0;"
                    + "if(aliases[field].some(a=>norm(attrs).includes(norm(a))))score+=140;"
                    + "if(aliases[field].some(a=>norm(own)===norm(a)))score+=130;"
                    + "else if(aliases[field].some(a=>norm(own).includes(norm(a)))&&norm(own).length<24)score+=90;"
                    + "if(aliases[field].some(a=>near.includes(norm(a))))score+=35;"
                    + "if(other[field].some(a=>norm(own).includes(norm(a))))score-=100;"
                    + "if(norm(own).length>45)score-=50;"
                    + "if(el.matches('input,select,button,[role=combobox],[role=button],[aria-haspopup],[tabindex]'))score+=8;"
                    + "return {el,own,score};"
                    + "}).filter(x=>x.score>0).sort((a,b)=>b.score-a.score||a.own.length-b.own.length);"
                    + "if(!candidates.length)return false;"
                    + "const best=candidates[0];best.el.setAttribute('data-dolatman-date-control',field);return true;"
                    + "}", field);

                if (found)
                    return (frame, frame.Locator($"[data-dolatman-date-control='{field}']").First);
            }
            catch
            {
                // Continue through frames; the caller reports which date control is missing.
            }
        }

        return (null, null);
    }

    private static string GetPersianMonthName(string month)
    {
        var names = new[]
        {
            "", "فروردین", "اردیبهشت", "خرداد", "تیر", "مرداد", "شهریور",
            "مهر", "آبان", "آذر", "دی", "بهمن", "اسفند"
        };
        if (!int.TryParse(NormalizeNumber(month), out var number) || number is < 1 or > 12)
            throw new InvalidOperationException("شماره ماه تولد معتبر نیست.");
        return names[number];
    }

    private async Task<bool> SelectDateControlAsync(IFrame frame, ILocator control, string value, string displayLabel)
    {
        var tag = await control.EvaluateAsync<string>("el => el.tagName.toLowerCase()");
        if (tag == "select")
        {
            var options = control.Locator("option");
            var count = await options.CountAsync();
            for (var i = 0; i < count; i++)
            {
                var option = options.Nth(i);
                var label = (await option.InnerTextAsync()).Trim();
                var optionValue = await option.GetAttributeAsync("value") ?? "";
                if (NormalizeForMatch(label) == NormalizeForMatch(displayLabel) ||
                    NormalizeNumber(label) == NormalizeNumber(value) ||
                    NormalizeNumber(optionValue) == NormalizeNumber(value))
                {
                    if (!string.IsNullOrEmpty(optionValue))
                        await control.SelectOptionAsync(optionValue, new LocatorSelectOptionOptions { Timeout = 5000 });
                    else
                        await control.SelectOptionAsync(new SelectOptionValue { Label = label });
                    return true;
                }
            }
            return false;
        }

        try
        {
            await control.ClickAsync(new LocatorClickOptions { Timeout = 5000 });
        }
        catch (PlaywrightException)
        {
            return false;
        }

        var clicked = await ClickVisibleDateOptionAsync(frame, value, displayLabel);
        if (!clicked)
            return false;

        await Task.Delay(120);
        string actual;
        try { actual = await control.InnerTextAsync(); }
        catch { actual = await ReadEditableValueAsync(control); }

        var normalizedActual = NormalizeForMatch(actual);
        var normalizedLabel = NormalizeForMatch(displayLabel);
        if (normalizedActual.Contains(normalizedLabel, StringComparison.Ordinal))
            return true;

        var normalizedValue = NormalizeNumber(actual);
        return normalizedValue.Contains(NormalizeNumber(value), StringComparison.Ordinal);
    }

    private static async Task<bool> ClickVisibleDateOptionAsync(IFrame frame, string value, string displayLabel)
    {
        var args = new { Value = value, Label = displayLabel };
        var found = await frame.EvaluateAsync<bool>("(args) => {"
            + "const norm=s=>(s||'').normalize('NFKC').replace(/[\\s\\u200c\\u200f\\u0640]/g,'').replace(/[ي]/g,'ی').replace(/[ك]/g,'ک');"
            + "const ascii=s=>(s||'').replace(/[۰-۹٠-٩]/g,ch=>{const a='۰۱۲۳۴۵۶۷۸۹',b='٠١٢٣٤٥٦٧٨٩';let i=a.indexOf(ch);if(i>=0)return String(i);i=b.indexOf(ch);return i>=0?String(i):ch;});"
            + "const num=s=>ascii(s).replace(/[\\s,٬]/g,'').replace(/^0+(?=\\d)/,'');"
            + "const visible=el=>{if(!el||!el.getClientRects().length)return false;const st=getComputedStyle(el);return st.display!=='none'&&st.visibility!=='hidden'&&st.opacity!=='0'&&!el.closest('[aria-hidden=true]');};"
            + "document.querySelectorAll('[data-dolatman-date-option]').forEach(el=>el.removeAttribute('data-dolatman-date-option'));"
            + "const wantedLabel=norm(args.Label),wantedNumber=num(args.Value);"
            + "const selector='option,[role=option],[role=menuitem],li,button,[role=button],[tabindex],div,span';"
            + "const matches=[...document.querySelectorAll(selector)].filter(visible).map(el=>{"
            + "const text=(el.innerText||el.textContent||'').trim();"
            + "const attrs=[el.getAttribute('value'),el.getAttribute('data-value'),el.getAttribute('aria-label'),el.getAttribute('title')].filter(Boolean).join(' ');"
            + "return {el,text,match:norm(text)===wantedLabel||num(text)===wantedNumber||norm(attrs)===wantedLabel||num(attrs)===wantedNumber};"
            + "}).filter(x=>x.match).sort((a,b)=>a.text.length-b.text.length);"
            + "if(!matches.length)return false;"
            + "let target=matches[0].el;"
            + "let actionable=target.closest('[role=option],[role=menuitem],li,button,[role=button],[tabindex]');"
            + "if(!actionable){for(let node=target,depth=0;node&&depth<5;node=node.parentElement,depth++){const st=getComputedStyle(node);if(node.onclick||st.cursor==='pointer'||/option|item|menu|dropdown/i.test(node.className?.toString()||'')){actionable=node;break;}}}"
            + "actionable ||= target;if(!visible(actionable))return false;actionable.setAttribute('data-dolatman-date-option','true');return true;"
            + "}", args);

        if (!found)
            return false;

        var option = frame.Locator("[data-dolatman-date-option='true']").First;
        try
        {
            await option.ClickAsync(new LocatorClickOptions { Timeout = 5000 });
            return true;
        }
        catch (PlaywrightException)
        {
            return false;
        }
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

    private static string NormalizeDigitsExact(string value)
        => ToAsciiDigits(value).Trim();

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
