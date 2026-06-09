using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace StarBridge.Desktop;

public sealed record LoginWindowAuthRequest(
    bool IsRegister,
    string Email,
    string Password,
    string? Callsign,
    string? VerificationCode);

public sealed record LoginWindowAuthResult(
    bool Success,
    string Message);

public partial class LoginWindow : Window
{
    private bool _isBusy;

    public Func<string, Task<string>>? SendVerificationCodeAsync { get; set; }

    public Func<LoginWindowAuthRequest, Task<LoginWindowAuthResult>>? AuthenticateAsync { get; set; }

    public LoginWindow(string? loginEmail)
    {
        InitializeComponent();
        LoginEmailBox.Text = loginEmail ?? "";
        RegisterEmailBox.Text = loginEmail ?? "";
        SetMode(isRegister: false);
        LoginEmailBox.Focus();
    }

    public bool IsRegisterMode { get; private set; }

    public bool IsSkipped { get; private set; }

    public string LoginEmail => LoginEmailBox.Text.Trim();

    public string LoginPassword => LoginPasswordBox.Password;

    public string RegisterEmail => RegisterEmailBox.Text.Trim();

    public string RegisterPassword => RegisterPasswordBox.Password;

    public string RegisterCallsign => RegisterCallsignBox.Text.Trim();

    public string RegisterVerificationCode => VerificationCodeBox.Text.Trim();

    private void LoginModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isBusy)
        {
            SetMode(isRegister: false);
        }
    }

    private void RegisterModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isBusy)
        {
            SetMode(isRegister: true);
        }
    }

    private void SetMode(bool isRegister)
    {
        IsRegisterMode = isRegister;
        LoginPanel.Visibility = isRegister ? Visibility.Collapsed : Visibility.Visible;
        RegisterPanel.Visibility = isRegister ? Visibility.Visible : Visibility.Collapsed;
        LoginModeButton.Style = (Style)FindResource(isRegister ? "SecondaryButton" : "PrimaryButton");
        RegisterModeButton.Style = (Style)FindResource(isRegister ? "PrimaryButton" : "SecondaryButton");
        ConfirmButton.Content = isRegister ? "注册" : "登录";
        SetStatus(isRegister
            ? "注册后将使用登录邮箱接收验证码，并把呼号绑定到你的星海舰桥个人身份。"
            : "使用注册邮箱登录。未登录时只能浏览，无法同步和管理舰队。");
    }

    private async void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        var request = BuildAuthRequest();
        if (request is null)
        {
            return;
        }

        if (AuthenticateAsync is null)
        {
            SetStatus("当前未连接登录服务。", isError: true);
            return;
        }

        SetBusy(true, IsRegisterMode ? "注册中..." : "登录中...");
        try
        {
            var result = await AuthenticateAsync(request);
            SetStatus(result.Message, !result.Success);
            if (result.Success)
            {
                DialogResult = true;
            }
        }
        catch (Exception ex)
        {
            SetStatus($"连接服务器失败：{ex.Message}", isError: true);
        }
        finally
        {
            if (DialogResult != true)
            {
                SetBusy(false);
            }
        }
    }

    private LoginWindowAuthRequest? BuildAuthRequest()
    {
        var email = IsRegisterMode ? RegisterEmail : LoginEmail;
        var password = IsRegisterMode ? RegisterPassword : LoginPassword;
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            SetStatus("请输入登录邮箱和密码。", isError: true);
            return null;
        }

        if (!LooksLikeEmail(email))
        {
            SetStatus("请输入有效的邮箱地址。", isError: true);
            return null;
        }

        if (!IsRegisterMode)
        {
            return new LoginWindowAuthRequest(false, email, password, null, null);
        }

        if (string.IsNullOrWhiteSpace(RegisterCallsign))
        {
            SetStatus("请输入呼号。", isError: true);
            return null;
        }

        if (string.IsNullOrWhiteSpace(RegisterVerificationCode))
        {
            SetStatus("请输入邮箱验证码。", isError: true);
            return null;
        }

        return new LoginWindowAuthRequest(true, email, password, RegisterCallsign, RegisterVerificationCode);
    }

    private async void SendCodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        if (SendVerificationCodeAsync is null)
        {
            SetStatus("当前未连接验证码服务。", isError: true);
            return;
        }

        var email = RegisterEmail;
        if (string.IsNullOrWhiteSpace(email) || !LooksLikeEmail(email))
        {
            SetStatus("请输入有效的注册邮箱。", isError: true);
            return;
        }

        SendCodeButton.IsEnabled = false;
        SendCodeButton.Content = "发送中...";
        SetStatus("正在向邮箱发送验证码...");
        try
        {
            var message = await SendVerificationCodeAsync(email);
            var isError = message.Contains("失败", StringComparison.OrdinalIgnoreCase) ||
                          message.Contains("错误", StringComparison.OrdinalIgnoreCase) ||
                          message.Contains("未配置", StringComparison.OrdinalIgnoreCase);
            SetStatus(message, isError);
            if (!isError)
            {
                await RunSendCodeCooldownAsync();
                return;
            }
        }
        catch (Exception ex)
        {
            SetStatus($"发送失败：{ex.Message}", isError: true);
        }
        finally
        {
            SendCodeButton.Content = "发送验证码";
            SendCodeButton.IsEnabled = true;
        }
    }

    private async Task RunSendCodeCooldownAsync()
    {
        for (var remaining = 60; remaining > 0; remaining--)
        {
            SendCodeButton.Content = $"{remaining}s";
            await Task.Delay(1000);
        }
    }

    private void SkipButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        IsSkipped = true;
        DialogResult = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isBusy)
        {
            DialogResult = false;
        }
    }

    private void SetBusy(bool isBusy, string? buttonText = null)
    {
        _isBusy = isBusy;
        LoginModeButton.IsEnabled = !isBusy;
        RegisterModeButton.IsEnabled = !isBusy;
        SkipButton.IsEnabled = !isBusy;
        CloseDialogButton.IsEnabled = !isBusy;
        ConfirmButton.IsEnabled = !isBusy;
        SendCodeButton.IsEnabled = !isBusy && IsRegisterMode;
        ConfirmButton.Content = buttonText ?? (IsRegisterMode ? "注册" : "登录");
    }

    private void SetStatus(string message, bool isError = false)
    {
        HintText.Text = message;
        HintText.Foreground = (System.Windows.Media.Brush)FindResource(isError ? "DangerBrush" : "MutedTextBrush");
    }

    private static bool LooksLikeEmail(string value)
    {
        var trimmed = value.Trim();
        var atIndex = trimmed.IndexOf('@');
        return atIndex > 0 &&
               atIndex < trimmed.Length - 3 &&
               trimmed.LastIndexOf('.') > atIndex + 1;
    }
}
