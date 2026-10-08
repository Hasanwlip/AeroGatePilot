using AeroGatePilot.Core.Access;

namespace AeroGatePilot.Portal;

/// <summary>Built-in portal strings. Templates reference them as {{t.key}}.</summary>
public static class PortalText
{
    private static readonly Dictionary<string, string> En = new()
    {
        ["username"] = "Username",
        ["password"] = "Password",
        ["sign_in"] = "Sign in",
        ["guest"] = "Continue as guest",
        ["or"] = "or",
        ["accept_terms"] = "I accept the terms of use",
        ["connected"] = "You're connected",
        ["connected_hint"] = "Enjoy the internet. Keep this page to check your remaining quota.",
        ["continue"] = "Continue browsing",
        ["logout"] = "Disconnect",
        ["plan"] = "Plan",
        ["data_used"] = "Data used",
        ["data_left"] = "Data left",
        ["time_left"] = "Time left",
        ["expires"] = "Valid until",
        ["speed"] = "Speed limit",
        ["download"] = "Download",
        ["upload"] = "Upload",
        ["session"] = "This session",
        ["unlimited"] = "Unlimited",
        ["welcome"] = "Welcome",
        ["show_password"] = "Show password",
        ["already_connected"] = "Connected — you can close this page.",
        ["error_InvalidCredentials"] = "Wrong username or password.",
        ["error_AccountDisabled"] = "This account is disabled.",
        ["error_AccountExpired"] = "This account has expired.",
        ["error_DataExhausted"] = "Your data allowance is used up.",
        ["error_TimeExhausted"] = "Your time allowance is used up.",
        ["error_SessionTimeLimit"] = "Session time limit reached. Please sign in again.",
        ["error_DeviceLimit"] = "Maximum number of devices for this account is already connected.",
        ["error_PlanMissing"] = "No plan is assigned to this account.",
        ["error_GuestDisabled"] = "Guest access is not available.",
        ["error_TooManyAttempts"] = "Too many attempts. Please wait a minute.",
        ["error_GatewayNotRunning"] = "The gateway is not running.",
        ["error_Preview"] = "Preview mode — sign in is disabled.",
        ["error_Generic"] = "Sign in failed.",
    };

    private static readonly Dictionary<string, string> Fa = new()
    {
        ["username"] = "نام کاربری",
        ["password"] = "رمز عبور",
        ["sign_in"] = "ورود",
        ["guest"] = "ورود به‌عنوان مهمان",
        ["or"] = "یا",
        ["accept_terms"] = "قوانین استفاده را می‌پذیرم",
        ["connected"] = "به اینترنت متصل شدید",
        ["connected_hint"] = "از اینترنت لذت ببرید. برای دیدن باقی‌مانده سهمیه، این صفحه را نگه دارید.",
        ["continue"] = "ادامه وب‌گردی",
        ["logout"] = "قطع اتصال",
        ["plan"] = "پلن",
        ["data_used"] = "حجم مصرف‌شده",
        ["data_left"] = "حجم باقی‌مانده",
        ["time_left"] = "زمان باقی‌مانده",
        ["expires"] = "اعتبار تا",
        ["speed"] = "محدودیت سرعت",
        ["download"] = "دانلود",
        ["upload"] = "آپلود",
        ["session"] = "این نشست",
        ["unlimited"] = "نامحدود",
        ["welcome"] = "خوش آمدید",
        ["show_password"] = "نمایش رمز",
        ["already_connected"] = "متصل هستید — می‌توانید این صفحه را ببندید.",
        ["error_InvalidCredentials"] = "نام کاربری یا رمز عبور اشتباه است.",
        ["error_AccountDisabled"] = "این حساب غیرفعال است.",
        ["error_AccountExpired"] = "اعتبار این حساب به پایان رسیده است.",
        ["error_DataExhausted"] = "حجم اینترنت شما تمام شده است.",
        ["error_TimeExhausted"] = "زمان اینترنت شما تمام شده است.",
        ["error_SessionTimeLimit"] = "زمان این نشست تمام شد. دوباره وارد شوید.",
        ["error_DeviceLimit"] = "حداکثر تعداد دستگاه مجاز برای این حساب متصل است.",
        ["error_PlanMissing"] = "هیچ پلنی به این حساب اختصاص داده نشده است.",
        ["error_GuestDisabled"] = "ورود مهمان فعال نیست.",
        ["error_TooManyAttempts"] = "تلاش‌های زیادی انجام شد. یک دقیقه صبر کنید.",
        ["error_GatewayNotRunning"] = "سرویس اینترنت فعال نیست.",
        ["error_Preview"] = "حالت پیش‌نمایش — ورود غیرفعال است.",
        ["error_Generic"] = "ورود ناموفق بود.",
    };

    public static IReadOnlyDictionary<string, string> For(string language) => language == "fa" ? Fa : En;

    public static string Error(string language, AccessDenyReason reason)
    {
        var table = For(language);
        return table.TryGetValue("error_" + reason, out var text) ? text : table["error_Generic"];
    }
}
