namespace RouteFinder.App;

internal static class UiLanguage
{
    static string FilePath => Path.Combine(ProfileStore.DirectoryPath, "language.txt");
    public static int Index { get; private set; }
    public static bool IsRightToLeft => Index is 1 or 2;
    static readonly Dictionary<string, (string Persian, string Arabic)> Labels = new(StringComparer.Ordinal)
    {
        ["Routes"] = ("مسیرها", "المسارات"),
        ["App router"] = ("مسیریاب برنامه‌ها", "موجّه التطبيقات"),
        ["What to route"] = ("ترافیک موردنظر", "حركة المرور"),
        ["Where to connect"] = ("مقصد اتصال", "وجهة الاتصال"),
        ["Advanced tools"] = ("ابزارهای پیشرفته", "الأدوات المتقدمة"),
        ["Connection options"] = ("گزینه‌های اتصال", "خيارات الاتصال"),
        ["Choose route"] = ("انتخاب مسیر", "اختر المسار"),
        ["+ Add route"] = ("+ افزودن مسیر", "+ أضف مسارًا"),
        ["+ Add app"] = ("+ افزودن برنامه", "+ أضف تطبيقًا"),
        ["Add route"] = ("افزودن مسیر", "أضف مسارًا"),
        ["Internet adapter"] = ("آداپتور اینترنت", "محول الإنترنت"),
        ["Automatic"] = ("خودکار", "تلقائي"),
        ["Saved adapter (unavailable)"] = ("آداپتور ذخیره‌شده (در دسترس نیست)", "المحول المحفوظ (غير متاح)"),
        ["No routes yet"] = ("هنوز مسیری اضافه نشده است", "لا توجد مسارات بعد"),
        ["Use Add route to import a configuration or sign in to a route provider."] = ("برای وارد کردن پیکربندی یا ورود به حساب ارائه‌دهنده، «افزودن مسیر» را انتخاب کنید.", "اختر «أضف مسارًا» لاستيراد إعداد أو تسجيل الدخول إلى مزوّد المسارات."),
        ["Add a route to get started."] = ("برای شروع یک مسیر اضافه کنید.", "أضف مسارًا للبدء."),
        ["Import WireGuard or Amnezia configuration…"] = ("وارد کردن پیکربندی WireGuard یا Amnezia…", "استورد إعداد WireGuard أو Amnezia…"),
        ["Sign in to EZConnect…"] = ("ورود به EZConnect…", "سجّل الدخول إلى EZConnect…"),
        ["Manage EZConnect account…"] = ("مدیریت حساب EZConnect…", "أدر حساب EZConnect…"),
        ["Add app"] = ("افزودن برنامه", "أضف تطبيقًا"),
        ["Test all routes"] = ("آزمایش همهٔ مسیرها", "اختبر كل المسارات"),
        ["Compare routes"] = ("مقایسهٔ مسیرها", "قارن المسارات"),
        ["Route options"] = ("گزینه‌های مسیر", "خيارات المسار"),
        ["Imported config"] = ("پیکربندی واردشده", "إعداد مستورد"),
        ["Choose traffic, choose a route, then connect. You can test a route before connecting."] = ("ترافیک و مسیر را انتخاب کنید و سپس متصل شوید. می‌توانید مسیر را پیش از اتصال آزمایش کنید.", "اختر حركة المرور والمسار ثم اتصل. يمكنك اختبار المسار قبل الاتصال."),
        ["Overview"] = ("نمای کلی", "نظرة عامة"),
        ["Route your apps"] = ("مسیریابی برنامه‌ها", "وجّه حركة تطبيقاتك"),
        ["Choose applications"] = ("انتخاب برنامه‌ها", "اختر التطبيقات"),
        ["Browse routes"] = ("مشاهدهٔ مسیرها", "تصفح المسارات"),
        ["Choose a route"] = ("انتخاب مسیر", "اختر مسارًا"),
        ["Import config"] = ("وارد کردن پیکربندی", "استورد الإعدادات"),
        ["Add EZConnect"] = ("افزودن EZConnect", "أضف EZConnect"),
        ["Add EZConnect account"] = ("افزودن حساب EZConnect", "أضف حساب EZConnect"),
        ["Sign in to EZConnect"] = ("ورود به EZConnect", "سجّل الدخول إلى EZConnect"),
        ["Source"] = ("منبع", "المصدر"),
        ["Config"] = ("پیکربندی", "الإعدادات"),
        ["Entire PC"] = ("کل رایانه", "الجهاز بالكامل"),
        ["Selected apps only"] = ("فقط برنامه‌های انتخاب‌شده", "التطبيقات المحددة فقط"),
        ["Other apps lose network access while connected."] = ("هنگام اتصال، دسترسی شبکه برای سایر برنامه‌ها قطع می‌شود.", "تفقد التطبيقات الأخرى الوصول إلى الشبكة أثناء الاتصال."),
        ["Choose what to route"] = ("انتخاب ترافیک", "اختر حركة المرور"),
        ["Route traffic from"] = ("مبدأ ترافیک", "مصدر حركة المرور"),
        ["Ready to connect."] = ("آمادهٔ اتصال", "جاهز للاتصال"),
        ["Connected"] = ("متصل", "متصل"),
        ["Disconnected"] = ("قطع اتصال", "غير متصل"),
        ["No peer response"] = ("بدون پاسخ از همتا", "لا استجابة من الطرف الآخر"),
        ["Waiting for peer"] = ("در انتظار پاسخ همتا", "بانتظار الطرف الآخر"),
        ["Cleanup needed"] = ("نیاز به پاک‌سازی", "يلزم التنظيف"),
        ["No routes match your search."] = ("مسیری مطابق جست‌وجوی شما یافت نشد.", "لا توجد مسارات تطابق بحثك."),
        ["No routes are available. Import a profile or refresh."] = ("مسیری در دسترس نیست. یک پروفایل وارد کنید یا فهرست را تازه کنید.", "لا توجد مسارات متاحة. استورد ملفًا أو حدّث القائمة."),
        ["Sign in to load your EZConnect routes, or import a local profile."] = ("برای بارگیری مسیرهای EZConnect وارد شوید یا یک پروفایل محلی وارد کنید.", "سجّل الدخول لتحميل مسارات EZConnect أو استورد ملفًا محليًا."),
        ["No route selected"] = ("مسیری انتخاب نشده است", "لم يُحدد مسار"),
        ["Sign in for EZConnect routes or import a profile."] = ("برای مسیرهای EZConnect وارد شوید یا یک پروفایل وارد کنید.", "سجّل الدخول لعرض مسارات EZConnect أو استورد ملفًا."),
        ["Endpoint available after allocation."] = ("نشانی مقصد پس از تخصیص مشخص می‌شود.", "يظهر عنوان نقطة الاتصال بعد التخصيص."),
        ["Endpoint"] = ("نشانی مقصد", "نقطة الاتصال"),
        ["Test for a WireGuard response."] = ("پاسخ WireGuard را آزمایش کنید.", "اختبر استجابة WireGuard."),
        ["No handshake after {0} traffic probe(s)."] = ("پس از {0} کاوش شبکه، دست‌دهی انجام نشد.", "لم تتم المصافحة بعد {0} محاولة فحص."),
        ["Responded"] = ("پاسخ دریافت شد", "تم تلقي استجابة"),
        ["No response"] = ("بدون پاسخ", "لا توجد استجابة"),
        ["Not tested"] = ("آزمایش نشده", "لم يُختبر"),
        ["Est. loss"] = ("اتلاف تخمینی", "الفقد المقدّر"),
        ["Jitter"] = ("نوسان", "التذبذب"),
        ["RTT spread"] = ("دامنهٔ زمان رفت‌وبرگشت", "مدى زمن الذهاب والإياب"),
        ["{0} shown   /   {1} available"] = ("{0} نمایش داده شده از {1} مسیر", "عرض {0} من أصل {1} مسارًا"),
        ["Route"] = ("مسیر", "المسار"),
        ["Region"] = ("منطقه", "المنطقة"),
        ["Last test"] = ("آخرین آزمایش", "آخر اختبار"),
        ["Selected apps"] = ("برنامه‌های انتخاب‌شده", "التطبيقات المحددة"),
        ["Entire PC (IPv4 + supported IPv6)"] = ("کل رایانه (IPv4 و IPv6 در صورت پشتیبانی)", "الجهاز بالكامل (IPv4 وIPv6 عند توفره)"),
        ["Sign in to RouteFinder"] = ("ورود به RouteFinder", "تسجيل الدخول إلى RouteFinder"),
        ["Remember me on this PC"] = ("مرا در این رایانه به خاطر بسپار", "تذكرني على هذا الجهاز"),
        ["Use your EZConnect account to load your route catalog."] = ("برای بارگیری مسیرها از حساب EZConnect خود استفاده کنید.", "استخدم حساب EZConnect لتحميل قائمة المسارات."),
        ["Enter your password"] = ("رمز عبور را وارد کنید", "أدخل كلمة المرور"),
        ["Connection workspace"] = ("محیط اتصال", "مساحة الاتصال"),
        ["ROUTE CATEGORY"] = ("دسته‌بندی مسیر", "فئة المسار"),
        ["ROUTE LIBRARY"] = ("فهرست مسیرها", "مكتبة المسارات"),
        ["Choose your connection"] = ("اتصال خود را انتخاب کنید", "اختر اتصالك"),
        ["Find running catalog app"] = ("یافتن برنامهٔ در حال اجرا", "البحث عن تطبيق قيد التشغيل"),
        ["Run network diagnostics"] = ("اجرای عیب‌یابی شبکه", "تشغيل تشخيص الشبكة"),
        ["Check for updates"] = ("بررسی به‌روزرسانی‌ها", "التحقق من التحديثات"),
        ["Mobile hotspot"] = ("نقطهٔ اتصال همراه", "نقطة اتصال الهواتف المحمولة"),
        ["Applications"] = ("برنامه‌ها", "التطبيقات"),
        ["Activity"] = ("فعالیت", "النشاط"),
        ["Settings"] = ("تنظیمات", "الإعدادات"),
        ["Sign in"] = ("ورود", "تسجيل الدخول"),
        ["Sign out"] = ("خروج", "تسجيل الخروج"),
        ["Connect"] = ("اتصال", "اتصال"),
        ["Disconnect"] = ("قطع اتصال", "قطع الاتصال"),
        ["Test route"] = ("آزمایش مسیر", "اختبار المسار"),
        ["Test routes"] = ("آزمایش مسیرها", "اختبار المسارات"),
        ["Cancel test"] = ("لغو آزمایش", "إلغاء الاختبار"),
        ["Retry cleanup"] = ("تلاش دوباره برای پاک‌سازی", "إعادة محاولة التنظيف"),
        ["Cancel"] = ("لغو", "إلغاء"),
        ["Forgot password?"] = ("رمز عبور را فراموش کرده‌اید؟", "هل نسيت كلمة المرور؟"),
        ["Email address"] = ("نشانی ایمیل", "عنوان البريد الإلكتروني"),
        ["Password"] = ("رمز عبور", "كلمة المرور"),
        ["All routes"] = ("همه مسیرها", "جميع المسارات"),
        ["Game routes"] = ("مسیرهای بازی", "مسارات الألعاب"),
        ["Download servers"] = ("سرورهای دانلود", "خوادم التنزيل"),
        ["Local profiles"] = ("پروفایل‌های محلی", "الملفات المحلية"),
        ["Language"] = ("زبان", "اللغة"),
        ["Apply language"] = ("اعمال زبان", "تطبيق اللغة"),
        ["Restart RouteFinder to apply the language."] = ("برای اعمال زبان، RouteFinder را دوباره اجرا کنید.", "أعد تشغيل RouteFinder لتطبيق اللغة."),
    };

    public static void Load()
    {
        try
        {
            Index = File.Exists(FilePath) ? File.ReadAllText(FilePath).Trim() switch
            {
                "fa" => 1,
                "ar" => 2,
                _ => 0
            } : 0;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void Save(int index)
    {
        Directory.CreateDirectory(ProfileStore.DirectoryPath);
        File.WriteAllText(FilePath, index switch { 1 => "fa", 2 => "ar", _ => "en" });
    }

    public static string Text(string original)
    {
        if (Index == 0 || !Labels.TryGetValue(original, out var translation)) return original;
        return Index == 1 ? translation.Persian : translation.Arabic;
    }
}
