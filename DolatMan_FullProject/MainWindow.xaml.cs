using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using DolatMan.Models;
using DolatMan.Services;

namespace DolatMan;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<Applicant> _applicants = new();
    private readonly SecureApplicantStore _store = new();
    private readonly ChromeAutomationService _chrome = new();
    private Applicant? _editingApplicant;
    private bool _isClosing;
    private CancellationTokenSource? _queueCancellation;
    private Applicant? _queueCurrentApplicant;

    public MainWindow()
    {
        InitializeComponent();
        ApplicantsGrid.ItemsSource = _applicants;
        AutomationApplicantCombo.ItemsSource = _applicants;
        MappingCombo.SelectedIndex = 0;
        AutomationMappingCombo.SelectedIndex = 0;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var recoveredInterruptedRun = false;
            foreach (var applicant in _store.Load())
            {
                // An interrupted run may have submitted the government form before the app closed.
                // Never auto-submit that record again until an operator reviews and resets it.
                if (applicant.QueueStatus == "در حال اجرا")
                {
                    applicant.QueueStatus = "نیازمند بررسی";
                    recoveredInterruptedRun = true;
                }
                _applicants.Add(applicant);
            }
            if (recoveredInterruptedRun) _store.Save(_applicants);
            RefreshCounts();
            AddActivity("برنامه اجرا شد و اطلاعات رمزگذاری‌شده بارگذاری شد.");
            SetStatus($"{_applicants.Count} پرونده از حافظه رمزگذاری‌شده بارگذاری شد.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "خطا در خواندن اطلاعات", MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus("خواندن پرونده‌های ذخیره‌شده ناموفق بود.");
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isClosing) return;
        if (_queueCancellation is not null)
        {
            e.Cancel = true;
            _queueCancellation.Cancel();
            QueueProgressText.Text = "ابتدا صف متوقف می‌شود؛ پس از پایان توقف دوباره پنجره را ببندید.";
            return;
        }
        _isClosing = true;
        try { _chrome.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        catch { /* closing the app must not be blocked by an already closed Chrome instance */ }
    }

    private void SetSection(string title, string subtitle, string section)
    {
        DashboardPanel.Visibility = section == "dashboard" ? Visibility.Visible : Visibility.Collapsed;
        ApplicantsPanel.Visibility = section == "applicants" ? Visibility.Visible : Visibility.Collapsed;
        AutomationPanel.Visibility = section == "automation" ? Visibility.Visible : Visibility.Collapsed;
        DocumentsPanel.Visibility = section == "documents" ? Visibility.Visible : Visibility.Collapsed;
        ReportsPanel.Visibility = section == "reports" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = section == "settings" ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = title;
        PageSubtitle.Text = subtitle;
    }

    private void NavDashboard_Click(object sender, RoutedEventArgs e)
        => SetSection("داشبورد اصلی", "مدیریت پرونده‌ها و راهبری ورود به سامانه دولت من", "dashboard");

    private void NavApplicants_Click(object sender, RoutedEventArgs e)
        => SetSection("مدیریت متقاضیان", "فهرست، جست‌وجو و ویرایش اطلاعات ذخیره‌شده", "applicants");

    private void NavAddApplicant_Click(object sender, RoutedEventArgs e)
    {
        ClearApplicantForm();
        SetSection("ثبت متقاضی جدید", "اطلاعات را مطابق مدرک معتبر وارد و ذخیره کنید", "applicants");
        NameBox.Focus();
    }

    private void NavAutomation_Click(object sender, RoutedEventArgs e)
        => SetSection("عملیات در Chrome", "باز کردن مرورگر، تکمیل کنترل‌شده فرم و ذخیره خروجی", "automation");

    private void NavDocuments_Click(object sender, RoutedEventArgs e)
        => SetSection("اسناد و PDFها", "فایل‌های حساب حقیقی هر متقاضی در پوشه مستقل ذخیره می‌شوند", "documents");

    private void NavReports_Click(object sender, RoutedEventArgs e)
        => SetSection("گزارش عملیات", "رویدادهای اجرای برنامه و عملیات مرورگر", "reports");

    private void NavSettings_Click(object sender, RoutedEventArgs e)
        => SetSection("تنظیمات و حریم خصوصی", "محل ذخیره اطلاعات و نکات امنیتی", "settings");

    private void SaveApplicant_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var mobile = MobileBox.Text.Trim();
        var uniqueId = UniqueIdBox.Text.Trim();
        var fida = FidaBox.Text.Trim();
        var birthDate = BirthDateBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(mobile) ||
            string.IsNullOrWhiteSpace(uniqueId) || string.IsNullOrWhiteSpace(fida) ||
            string.IsNullOrWhiteSpace(birthDate))
        {
            MessageBox.Show("نام، شماره موبایل، شناسه یکتا، کد فیدا و تاریخ تولد الزامی هستند.",
                "اطلاعات ناقص", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var mapping = Math.Clamp(MappingCombo.SelectedIndex, 0, 2);
        var isNewApplicant = _editingApplicant is null;
        var applicant = _editingApplicant ?? new Applicant();

        // Keep a rollback snapshot so a failed disk write never leaves unsaved edits in memory.
        var oldFullName = applicant.FullName;
        var oldMobile = applicant.Mobile;
        var oldUniqueId = applicant.UniqueId;
        var oldFidaCode = applicant.FidaCode;
        var oldBirthDate = applicant.BirthDate;
        var oldMappingIndex = applicant.MappingIndex;
        var oldQueueStatus = applicant.QueueStatus;

        var recordChanged = !isNewApplicant &&
            (oldFullName != name || oldMobile != mobile || oldUniqueId != uniqueId ||
             oldFidaCode != fida || oldBirthDate != birthDate || oldMappingIndex != mapping);

        applicant.FullName = name;
        applicant.Mobile = mobile;
        applicant.UniqueId = uniqueId;
        applicant.FidaCode = fida;
        applicant.BirthDate = birthDate;
        applicant.MappingIndex = mapping;

        // Editing a successfully processed record must never silently put it back in the queue.
        // An operator must review the portal and explicitly use "Prepare again" if a retry is needed.
        if (recordChanged && oldQueueStatus == "موفق")
            applicant.QueueStatus = "نیازمند بررسی";

        if (isNewApplicant) _applicants.Add(applicant);

        try
        {
            _store.Save(_applicants);
            AutomationApplicantCombo.SelectedItem = applicant;
            AutomationMappingCombo.SelectedIndex = mapping;
            ApplicantsGrid.SelectedItem = applicant;
            RefreshCounts();
            AddActivity("پرونده متقاضی ذخیره شد.");
            SetStatus("اطلاعات متقاضی با رمزگذاری ویندوز ذخیره شد.");
            MessageBox.Show("پرونده با موفقیت و به‌صورت رمزگذاری‌شده ذخیره شد.",
                "ذخیره موفق", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            if (isNewApplicant)
            {
                _applicants.Remove(applicant);
            }
            else
            {
                applicant.FullName = oldFullName;
                applicant.Mobile = oldMobile;
                applicant.UniqueId = oldUniqueId;
                applicant.FidaCode = oldFidaCode;
                applicant.BirthDate = oldBirthDate;
                applicant.MappingIndex = oldMappingIndex;
                applicant.QueueStatus = oldQueueStatus;
            }
            MessageBox.Show("ذخیره اطلاعات ناموفق بود؛ تغییرات به حالت قبلی برگشتند:\n" + ex.Message,
                "خطا در ذخیره", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearApplicant_Click(object sender, RoutedEventArgs e) => ClearApplicantForm();

    private void ClearApplicantForm()
    {
        _editingApplicant = null;
        ApplicantsGrid.SelectedItem = null;
        NameBox.Clear();
        MobileBox.Clear();
        UniqueIdBox.Clear();
        FidaBox.Clear();
        BirthDateBox.Clear();
        MappingCombo.SelectedIndex = 0;
    }

    private void DeleteApplicant_Click(object sender, RoutedEventArgs e)
    {
        if (ApplicantsGrid.SelectedItem is not Applicant selected)
        {
            MessageBox.Show("ابتدا یک متقاضی را از فهرست انتخاب کنید.", "انتخاب متقاضی",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show($"پرونده «{selected.FullName}» حذف شود؟", "تأیید حذف",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var originalIndex = _applicants.IndexOf(selected);
        _applicants.Remove(selected);
        try
        {
            _store.Save(_applicants);
            ClearApplicantForm();
            RefreshCounts();
            AddActivity("یک پرونده از فهرست حذف شد.");
            SetStatus("پرونده انتخاب‌شده حذف شد.");
        }
        catch (Exception ex)
        {
            _applicants.Insert(Math.Clamp(originalIndex, 0, _applicants.Count), selected);
            ApplicantsGrid.SelectedItem = selected;
            MessageBox.Show("ذخیره تغییرات پس از حذف ناموفق بود؛ پرونده به فهرست برگشت:\n" + ex.Message,
                "خطا", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }


    private void ResetApplicantQueueStatus_Click(object sender, RoutedEventArgs e)
    {
        if (ApplicantsGrid.SelectedItem is not Applicant selected)
        {
            MessageBox.Show("ابتدا پرونده موردنظر را از فهرست انتخاب کنید.", "انتخاب متقاضی",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var warning = selected.QueueStatus == "موفق"
            ? "این پرونده قبلاً موفق ثبت شده است. اجرای دوباره ممکن است درخواست تکراری ایجاد کند. فقط پس از بررسی سوابق سامانه ادامه دهید. آماده‌سازی مجدد انجام شود؟"
            : "وضعیت پرونده به «آماده» برمی‌گردد. پیش از اجرا، وضعیت فعلی درخواست را در سامانه بررسی کنید تا ثبت تکراری رخ ندهد. ادامه می‌دهید؟";
        if (MessageBox.Show(warning, "تأیید آماده‌سازی مجدد", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        selected.QueueStatus = "آماده";
        try
        {
            _store.Save(_applicants);
            RefreshCounts();
            AddActivity($"پرونده {selected.FullName} پس از بررسی اپراتور برای صف آماده شد.");
            SetStatus($"پرونده {selected.FullName} آماده اجرا شد.");
            MessageBox.Show("پرونده برای اجرای بعدی آماده شد.", "آماده‌سازی موفق",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            selected.QueueStatus = "نیازمند بررسی";
            MessageBox.Show("ذخیره وضعیت ناموفق بود:\n" + ex.Message, "خطا",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplicantsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ApplicantsGrid.SelectedItem is not Applicant selected) return;
        _editingApplicant = selected;
        NameBox.Text = selected.FullName;
        MobileBox.Text = selected.Mobile;
        UniqueIdBox.Text = selected.UniqueId;
        FidaBox.Text = selected.FidaCode;
        BirthDateBox.Text = selected.BirthDate;
        MappingCombo.SelectedIndex = Math.Clamp(selected.MappingIndex, 0, 2);
        AutomationApplicantCombo.SelectedItem = selected;
        AutomationMappingCombo.SelectedIndex = Math.Clamp(selected.MappingIndex, 0, 2);
    }

    private async void StartQueue_Click(object sender, RoutedEventArgs e)
    {
        if (_queueCancellation is not null) return;

        // Only explicitly prepared records run automatically. Interrupted/failed records require
        // a human review and an explicit reset from the applicant panel to prevent duplicate submissions.
        var pending = _applicants.Where(a => string.Equals(a.QueueStatus, "آماده", StringComparison.OrdinalIgnoreCase)).ToList();
        if (pending.Count == 0)
        {
            MessageBox.Show("پرونده آماده‌ای برای اجرا وجود ندارد. پرونده‌های موفق اجرا نمی‌شوند و پرونده‌های نیازمند بررسی باید ابتدا بررسی و سپس از بخش مدیریت متقاضیان آماده‌سازی شوند.",
                "صف آماده نیست", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var missing = pending.FirstOrDefault(a => string.IsNullOrWhiteSpace(a.Mobile) ||
            string.IsNullOrWhiteSpace(a.FullName) || string.IsNullOrWhiteSpace(a.UniqueId) ||
            string.IsNullOrWhiteSpace(a.FidaCode) || string.IsNullOrWhiteSpace(a.BirthDate));
        if (missing is not null)
        {
            MessageBox.Show($"پرونده «{missing.FullName}» ناقص است. نام، موبایل، شناسه یکتا، کد فیدا و تاریخ تولد را تکمیل کنید.",
                "اطلاعات ناقص", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var consent = MessageBox.Show(
            $"قرار است {pending.Count} پرونده به‌ترتیب اجرا شود. برنامه برای هر فرد صفحه ورود را باز می‌کند، شماره موبایل و کد امنیتی را وارد/ارسال می‌کند، پس از ورود پیامکی فرم اتباع را با روش ذخیره‌شده پر می‌کند، دکمه ثبت اطلاعات را می‌زند و در صورت رسیدن به داشبورد، حساب حقیقی را ذخیره می‌کند.\n\nکد امنیتی و سپس کد پیامکی دریافتی را در پنجره‌های DolatMan وارد می‌کنید؛ برنامه آن‌ها را در صفحه رسمی Chrome تایپ می‌کند.\n\nتأیید می‌کنید اطلاعات متعلق به همین متقاضیان و مجاز و صحیح است و اجازه ثبت فرم‌ها را می‌دهید؟",
            "تأیید اجرای خودکار پرونده‌ها", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (consent != MessageBoxResult.Yes) return;

        _queueCancellation = new CancellationTokenSource();
        StartQueueButton.IsEnabled = false;
        StopQueueButton.IsEnabled = true;
        QueueProgressBar.Minimum = 0;
        QueueProgressBar.Maximum = pending.Count;
        QueueProgressBar.Value = 0;
        IProgress<string> progress = new Progress<string>(message =>
        {
            BrowserStatusText.Text = message;
            BrowserUrlText.Text = _chrome.CurrentUrl;
            QueueProgressText.Text = message;
            SetStatus(message);
        });

        var completed = 0;
        try
        {
            for (var index = 0; index < pending.Count; index++)
            {
                _queueCancellation.Token.ThrowIfCancellationRequested();
                var applicant = pending[index];
                _queueCurrentApplicant = applicant;
                applicant.QueueStatus = "در حال اجرا";
                _store.Save(_applicants);
                AddActivity($"شروع پرونده {applicant.FullName} ({index + 1} از {pending.Count}).");
                QueueProgressText.Text = $"پرونده {applicant.FullName} — {index + 1} از {pending.Count}";

                // Always start each identity in a clean isolated browser session. This also clears
                // a possibly interrupted session left behind by a previous failed attempt.
                progress.Report("در حال پاک‌سازی نشست مرورگر قبلی و آماده‌سازی Chrome...");
                await _chrome.ResetSessionForNextApplicantAsync();

                var result = await _chrome.ProcessApplicantFlowAsync(
                    applicant,
                    RequestCaptchaFromOperatorAsync,
                    RequestOtpFromOperatorAsync,
                    progress,
                    _queueCancellation.Token);

                applicant.QueueStatus = "موفق";
                _store.Save(_applicants);
                completed++;
                QueueProgressBar.Value = completed;
                RefreshCounts();
                AddActivity($"پرونده {applicant.FullName} با موفقیت پردازش و خروجی ذخیره شد.");
                BrowserStatusText.Text = result;
                QueueProgressText.Text = $"موفق: {applicant.FullName} — {completed} از {pending.Count}";
                SetStatus($"پرونده {applicant.FullName} تکمیل شد.");
                _queueCurrentApplicant = null;

                progress.Report(index < pending.Count - 1
                    ? "در حال بستن نشست قبلی و آماده‌سازی Chrome برای نفر بعدی..."
                    : "در حال بستن نشست متقاضی آخر و پاک‌سازی پروفایل مرورگر...");
                await _chrome.ResetSessionForNextApplicantAsync();
            }

            QueueProgressText.Text = $"پایان صف: {completed} پرونده با موفقیت تکمیل شد.";
            BrowserStatusText.Text = QueueProgressText.Text;
            AddActivity(QueueProgressText.Text);
            MessageBox.Show(QueueProgressText.Text, "پایان اجرای صف", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            if (_queueCurrentApplicant is not null && _queueCurrentApplicant.QueueStatus == "در حال اجرا")
                _queueCurrentApplicant.QueueStatus = "متوقف‌شده توسط کاربر";
            try { _store.Save(_applicants); } catch { }
            QueueProgressText.Text = $"صف متوقف شد. پرونده‌های موفق: {completed} از {pending.Count}";
            BrowserStatusText.Text = QueueProgressText.Text;
            AddActivity("اجرای صف توسط کاربر متوقف شد؛ Chrome برای بررسی وضعیت فعلی باز می‌ماند.");
            SetStatus("صف متوقف شد؛ پرونده‌های موفق ذخیره شده‌اند.");
        }
        catch (Exception ex)
        {
            if (_queueCurrentApplicant is not null)
                _queueCurrentApplicant.QueueStatus = "نیازمند بررسی";
            try { _store.Save(_applicants); } catch { }
            QueueProgressText.Text = $"صف در پرونده «{_queueCurrentApplicant?.FullName}» متوقف شد؛ پرونده‌های موفق: {completed} از {pending.Count}";
            BrowserStatusText.Text = ex.Message;
            BrowserUrlText.Text = _chrome.CurrentUrl;
            AddActivity($"صف در پرونده {_queueCurrentApplicant?.FullName} متوقف شد: {ex.Message}");
            SetStatus("یک پرونده نیازمند بررسی است؛ برای جلوگیری از ثبت اشتباه ادامه صف متوقف شد.");
            MessageBox.Show(ex.Message + "\n\nروش ذخیره‌شده را خودکار تغییر ندادیم و روش‌های دیگر را امتحان نکردیم. پس از بررسی مدرک و خطای سامانه، اطلاعات را اصلاح کنید و دوباره صف را اجرا کنید.",
                "توقف ایمن عملیات", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _queueCurrentApplicant = null;
            _queueCancellation?.Dispose();
            _queueCancellation = null;
            StartQueueButton.IsEnabled = true;
            StopQueueButton.IsEnabled = false;
            RefreshCounts();
        }
    }

    private void StopQueue_Click(object sender, RoutedEventArgs e)
    {
        _queueCancellation?.Cancel();
        QueueProgressText.Text = "درخواست توقف دریافت شد؛ عملیات جاری در اولین نقطه امن متوقف می‌شود.";
        SetStatus("در حال توقف صف...");
    }

    private Task<string?> RequestCaptchaFromOperatorAsync(byte[]? captchaImage)
    {
        var dialog = new Window
        {
            Title = "ورود کد امنیتی دولت من",
            Owner = this,
            Width = 470,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            FlowDirection = FlowDirection.RightToLeft,
            Background = System.Windows.Media.Brushes.White,
            ShowInTaskbar = false
        };

        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock
        {
            Text = captchaImage is { Length: > 0 }
                ? "تصویر کد امنیتی سامانه در زیر نمایش داده شده است. کد را خودتان بخوانید و وارد کنید."
                : "تصویر کد امنیتی از صفحه پیدا نشد. Chrome را بررسی کنید؛ سپس کد را از تصویر سامانه وارد کنید.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            Foreground = System.Windows.Media.Brushes.DarkSlateGray,
            Margin = new Thickness(0, 0, 0, 14)
        });
        if (captchaImage is { Length: > 0 })
        {
            try
            {
                var bitmap = new BitmapImage();
                using (var stream = new MemoryStream(captchaImage))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                }
                panel.Children.Add(new System.Windows.Controls.Image
                {
                    Source = bitmap,
                    Stretch = System.Windows.Media.Stretch.Uniform,
                    MaxWidth = 360,
                    MaxHeight = 150,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 14)
                });
            }
            catch { /* the operator can still read the CAPTCHA in Chrome */ }
        }
        var codeBox = new TextBox
        {
            Height = 42,
            FontSize = 18,
            Padding = new Thickness(10, 6, 10, 6),
            FlowDirection = FlowDirection.LeftToRight,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            MaxLength = 12
        };
        panel.Children.Add(codeBox);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0) };
        var continueButton = new Button { Content = "ارسال کد و ادامه خودکار", Padding = new Thickness(16, 9, 16, 9), MinWidth = 160, IsDefault = true, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 105, 178)), Foreground = System.Windows.Media.Brushes.White, BorderThickness = new Thickness(0) };
        var cancelButton = new Button { Content = "لغو صف", Padding = new Thickness(16, 9, 16, 9), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        continueButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(codeBox.Text))
            {
                MessageBox.Show(dialog, "کد امنیتی را وارد کنید.", "کد خالی", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            dialog.DialogResult = true;
        };
        buttons.Children.Add(continueButton);
        buttons.Children.Add(cancelButton);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.Loaded += (_, _) => codeBox.Focus();

        var accepted = dialog.ShowDialog() == true;
        return Task.FromResult(accepted ? codeBox.Text.Trim() : null);
    }

    private Task<string?> RequestOtpFromOperatorAsync()
    {
        var dialog = new Window
        {
            Title = "کد پیامکی دولت من",
            Owner = this,
            Width = 430,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            FlowDirection = FlowDirection.RightToLeft,
            Background = System.Windows.Media.Brushes.White,
            ShowInTaskbar = false
        };
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock
        {
            Text = "درخواست ارسال کد پیامکی از سامانه انجام شد. کدی را که برای شماره همراه همین متقاضی دریافت کرده‌اید وارد کنید.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 14),
            Foreground = System.Windows.Media.Brushes.DarkSlateGray
        });
        var codeBox = new TextBox
        {
            Height = 44,
            FontSize = 20,
            MaxLength = 8,
            FlowDirection = FlowDirection.LeftToRight,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(10, 6, 10, 6)
        };
        panel.Children.Add(codeBox);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        var confirm = new Button { Content = "تأیید و ادامه", MinWidth = 130, Padding = new Thickness(14, 9, 14, 9), IsDefault = true,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 105, 178)),
            Foreground = System.Windows.Media.Brushes.White, BorderThickness = new Thickness(0) };
        var cancel = new Button { Content = "لغو", MinWidth = 80, Padding = new Thickness(14, 9, 14, 9), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        confirm.Click += (_, _) =>
        {
            var digits = new string(codeBox.Text.Where(char.IsDigit).ToArray());
            if (digits.Length < 4 || digits.Length > 8)
            {
                MessageBox.Show(dialog, "کد پیامکی معتبر را وارد کنید.", "کد نامعتبر", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            codeBox.Text = digits;
            dialog.DialogResult = true;
        };
        buttons.Children.Add(confirm);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.Loaded += (_, _) => codeBox.Focus();
        return Task.FromResult(dialog.ShowDialog() == true ? codeBox.Text.Trim() : null);
    }

    private async void StartChrome_Click(object sender, RoutedEventArgs e)
    {
        BrowserStatusText.Text = "در حال باز کردن درگاه دولت من و انتخاب «ورود به پنجره ملی» در Chrome...";
        BrowserUrlText.Text = ChromeAutomationService.LoginUrl;
        SetStatus("در حال اجرای Chrome و عبور از صفحه انتخاب خدمت دولت من...");

        await RunBrowserActionAsync(async () =>
        {
            var message = await _chrome.OpenLoginAsync();
            ChromeStateText.Text = "باز";
            return message;
        });
    }

    private async void FillMobile_Click(object sender, RoutedEventArgs e)
    {
        var applicant = GetSelectedApplicant();
        if (applicant is null) return;
        await RunBrowserActionAsync(async () =>
        {
            await _chrome.FillMobileAsync(applicant.Mobile);
            return "شماره موبایل وارد شد. برای ادامه صف، کد امنیتی و کد پیامکی را در پنجره‌های DolatMan وارد کنید.";
        });
    }

    private async void OpenRegister_Click(object sender, RoutedEventArgs e)
    {
        await RunBrowserActionAsync(async () =>
        {
            await _chrome.OpenRegisterAfterManualLoginAsync();
            return "گزینه «اتباع» انتخاب و تأیید شد؛ اکنون فرم آماده ورود اطلاعات است.";
        });
    }

    private async void FillIdentity_Click(object sender, RoutedEventArgs e)
    {
        var applicant = GetSelectedApplicant();
        if (applicant is null) return;
        var mapping = Math.Clamp(AutomationMappingCombo.SelectedIndex, 0, 2);
        applicant.MappingIndex = mapping;
        try { _store.Save(_applicants); }
        catch (Exception ex)
        {
            MessageBox.Show("ذخیره روش انتخابی ناموفق بود:\n" + ex.Message, "خطا", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        await RunBrowserActionAsync(async () => await _chrome.FillIdentityFormAsync(applicant, mapping));
    }

    private async void OpenDashboard_Click(object sender, RoutedEventArgs e)
    {
        await RunBrowserActionAsync(async () =>
        {
            await _chrome.OpenDashboardAsync();
            return "داشبورد باز شد. بررسی کنید که ورود متقاضی موفق بوده باشد.";
        });
    }

    private async void OpenAccount_Click(object sender, RoutedEventArgs e)
    {
        await RunBrowserActionAsync(async () =>
        {
            await _chrome.OpenIndividualAccountAsync();
            return "صفحه حساب کاربری حقیقی باز شد. مشخصات را با متقاضی انتخاب‌شده تطبیق دهید.";
        });
    }

    private async void SaveAccount_Click(object sender, RoutedEventArgs e)
    {
        var applicant = GetSelectedApplicant();
        if (applicant is null) return;
        await RunBrowserActionAsync(async () =>
        {
            var message = await _chrome.SaveIndividualAccountAsync(applicant);
            RefreshCounts();
            AddActivity("خروجی صفحه حساب حقیقی ذخیره شد.");
            return message;
        });
    }

    private async void CheckPage_Click(object sender, RoutedEventArgs e)
    {
        await RunBrowserActionAsync(async () => await _chrome.GetPageStatusAsync());
    }

    private async Task RunBrowserActionAsync(Func<Task<string>> action)
    {
        try
        {
            var message = await action();
            BrowserStatusText.Text = message;
            BrowserUrlText.Text = _chrome.CurrentUrl;
            SetStatus(message.Replace("\n", " "));
            AddActivity("عملیات Chrome اجرا شد.");
        }
        catch (Exception ex)
        {
            BrowserStatusText.Text = ex.Message;
            BrowserUrlText.Text = _chrome.CurrentUrl;
            SetStatus("عملیات مرورگر متوقف شد؛ پیام خطا را بررسی کنید.");
            AddActivity("یک عملیات Chrome با خطا یا توقف مواجه شد.");
            MessageBox.Show(ex.Message, "عملیات انجام نشد", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private Applicant? GetSelectedApplicant()
    {
        if (AutomationApplicantCombo.SelectedItem is Applicant selected) return selected;
        MessageBox.Show("ابتدا متقاضی موردنظر را از فهرست انتخاب کنید.", "انتخاب متقاضی",
            MessageBoxButton.OK, MessageBoxImage.Information);
        return null;
    }

    private void OpenExports_Click(object sender, RoutedEventArgs e)
        => OpenFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DolatMan", "خروجی متقاضیان"));

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
        => OpenFolder(_store.DataDirectory);

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("باز کردن پوشه ناموفق بود:\n" + ex.Message, "خطا", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RefreshCounts()
    {
        ApplicantCountText.Text = _applicants.Count.ToString("N0");
        var exportRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DolatMan", "خروجی متقاضیان");
        var exportCount = 0;
        if (Directory.Exists(exportRoot))
        {
            try { exportCount = Directory.EnumerateFiles(exportRoot, "*.*", SearchOption.AllDirectories)
                .Count(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)); }
            catch { /* the summary count is optional */ }
        }
        ExportCountText.Text = exportCount.ToString("N0");
        ChromeStateText.Text = _chrome.IsOpen ? "باز" : "بسته";
    }

    private void AddActivity(string message)
    {
        var line = $"{DateTime.Now:yyyy/MM/dd HH:mm:ss}  —  {message}";
        ActivityList.Items.Insert(0, line);
        while (ActivityList.Items.Count > 200) ActivityList.Items.RemoveAt(ActivityList.Items.Count - 1);
    }

    private void SetStatus(string message) => StatusText.Text = message;
}
