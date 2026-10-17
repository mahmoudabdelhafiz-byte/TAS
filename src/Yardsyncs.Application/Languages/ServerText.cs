using System.Text.RegularExpressions;

namespace Yardsyncs.Application.Languages;

/// <summary>
/// The server's own sentences — booking check results, gate decisions, the "ready to go" reason, errors — in the
/// language of the request (Accept-Language). They are written in English where they arise (and stored that way, e.g.
/// in a trouble-desk case), and put into the reader's language on the way out, sentence by sentence; anything not in
/// the catalog stays English. Numbers, container numbers and plates are kept as they are. Arabic covers the portal;
/// Urdu and Hindi cover what drivers read. All wording to be reviewed by native speakers.
/// </summary>
public static class ServerText
{
    private static readonly AsyncLocal<string?> CurrentLanguage = new();

    /// <summary>The language of the request being answered ("en" when not set).</summary>
    public static string Language
    {
        get => CurrentLanguage.Value ?? "en";
        set => CurrentLanguage.Value = value;
    }

    /// <summary>From an Accept-Language header: the first language with a catalog, else English.</summary>
    public static string FromAcceptLanguage(string? header)
    {
        foreach (var part in (header ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tag = part.Split(';')[0].Split('-')[0].Trim().ToLowerInvariant();
            if (tag == "en" || Catalogs.ContainsKey(tag))
                return tag;
        }
        return "en";
    }

    /// <summary>The text in the current request's language.</summary>
    public static string T(string text) => Translate(text, Language);

    public static string? TOrNull(string? text) => text is null ? null : T(text);

    public static IReadOnlyList<string> T(IEnumerable<string> texts) => texts.Select(T).ToList();

    public static string Translate(string text, string language)
    {
        if (string.IsNullOrEmpty(text) || !Catalogs.TryGetValue(language, out var catalog))
            return text;
        foreach (var (pattern, replacement) in catalog)
            text = replacement.StartsWith(DatePrefix, StringComparison.Ordinal)
                ? pattern.Replace(text, m => m.Result(replacement[DatePrefix.Length..]).Replace("{date}", Dates(m.Groups["date"].Value, language)))
                : pattern.Replace(text, replacement);
        return text;
    }

    /// <summary>How many sentences a language has (for tests and the language template).</summary>
    public static int Count(string language) => Catalogs.TryGetValue(language, out var c) ? c.Count : 0;

    // A replacement starting with this has {date} for the "ddd dd MMM HH:mm" group named date, in the language's words.
    private const string DatePrefix = "date:";

    private static readonly Dictionary<string, Dictionary<string, string>> DateNames = new()
    {
        ["ar"] = new()
        {
            ["Sat"] = "السبت", ["Sun"] = "الأحد", ["Mon"] = "الاثنين", ["Tue"] = "الثلاثاء", ["Wed"] = "الأربعاء", ["Thu"] = "الخميس", ["Fri"] = "الجمعة",
            ["Jan"] = "يناير", ["Feb"] = "فبراير", ["Mar"] = "مارس", ["Apr"] = "أبريل", ["May"] = "مايو", ["Jun"] = "يونيو",
            ["Jul"] = "يوليو", ["Aug"] = "أغسطس", ["Sep"] = "سبتمبر", ["Oct"] = "أكتوبر", ["Nov"] = "نوفمبر", ["Dec"] = "ديسمبر",
        },
        ["ur"] = new()
        {
            ["Sat"] = "ہفتہ", ["Sun"] = "اتوار", ["Mon"] = "پیر", ["Tue"] = "منگل", ["Wed"] = "بدھ", ["Thu"] = "جمعرات", ["Fri"] = "جمعہ",
            ["Jan"] = "جنوری", ["Feb"] = "فروری", ["Mar"] = "مارچ", ["Apr"] = "اپریل", ["May"] = "مئی", ["Jun"] = "جون",
            ["Jul"] = "جولائی", ["Aug"] = "اگست", ["Sep"] = "ستمبر", ["Oct"] = "اکتوبر", ["Nov"] = "نومبر", ["Dec"] = "دسمبر",
        },
        ["hi"] = new()
        {
            ["Sat"] = "शनिवार", ["Sun"] = "रविवार", ["Mon"] = "सोमवार", ["Tue"] = "मंगलवार", ["Wed"] = "बुधवार", ["Thu"] = "गुरुवार", ["Fri"] = "शुक्रवार",
            ["Jan"] = "जनवरी", ["Feb"] = "फ़रवरी", ["Mar"] = "मार्च", ["Apr"] = "अप्रैल", ["May"] = "मई", ["Jun"] = "जून",
            ["Jul"] = "जुलाई", ["Aug"] = "अगस्त", ["Sep"] = "सितंबर", ["Oct"] = "अक्टूबर", ["Nov"] = "नवंबर", ["Dec"] = "दिसंबर",
        },
    };

    private static readonly Regex DateWord = R(@"\b(Sat|Sun|Mon|Tue|Wed|Thu|Fri|Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)\b");

    /// <summary>Day and month names of a date label ("Tue 20 Oct 10:00–11:00") in the language; numbers kept.</summary>
    public static string Dates(string text, string language) =>
        string.IsNullOrEmpty(text) || !DateNames.TryGetValue(language, out var names)
            ? text
            : DateWord.Replace(text, m => names[m.Value]);

    private static Regex R(string pattern) => new(pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Longer, more specific sentences first: a shorter one may be part of them.
    private static readonly Dictionary<string, List<(Regex Pattern, string Replacement)>> Catalogs = new()
    {
        ["ar"] =
        [
            // ---- the terminal operating system's checks (demo TOS and Navis N4)
            (R(@"Customs hold on (\S+)"), "حجز جمركي على $1"),
            (R(@"Shipping line has not released (\S+)"), "لم يُفرج الخط الملاحي عن $1"),
            (R(@"Terminal hold on (\S+)"), "حجز من المحطة على $1"),
            (R(@"Storage charges on (\S+) start today"), "رسوم التخزين على $1 تبدأ اليوم"),
            (R(@"Charges for (\S+) are unpaid"), "رسوم $1 غير مدفوعة"),
            (R(@"(\S+) is not available for pickup yet"), "$1 غير متاحة للاستلام بعد"),
            (R(@"Cargo (\S+) is not released by customs"), "البضاعة $1 غير مُفرج عنها جمركياً"),
            (R(@"Cargo (\S+) is not released for delivery"), "البضاعة $1 غير مُفرج عنها للتسليم"),
            (R(@"Cargo (\S+) is not known in N4"), "البضاعة $1 غير معروفة في نظام التشغيل"),
            (R(@"Cargo cut-off for booking (\S+) is (.+?), before this slot"), "موعد إغلاق البضاعة للحجز $1 هو $2، قبل هذه الفترة"),
            (R(@"Cut-off has passed for booking (\S+)"), "انتهى موعد الإغلاق للحجز $1"),
            (R(@"Booking (\S+) is not known in N4"), "الحجز $1 غير معروف في نظام التشغيل"),
            (R(@"Booking (\S+) has no containers left to receive"), "لا توجد حاويات متبقية للاستلام في الحجز $1"),
            (R(@"Booking (\S+) is not valid"), "الحجز $1 غير صالح"),
            (R(@"Empty release (\S+) is not known in N4"), "إذن الحاوية الفارغة $1 غير معروف في نظام التشغيل"),
            (R(@"Empty release (\S+) has no containers left"), "لا توجد حاويات متبقية في إذن الحاوية الفارغة $1"),
            (R(@"Empty release (\S+) is not valid"), "إذن الحاوية الفارغة $1 غير صالح"),
            (R(@"No VGM for (\S+) in N4 yet — it must be declared before gate-in"), "لا يوجد وزن مُتحقق (VGM) للحاوية $1 في نظام التشغيل بعد — يجب التصريح به قبل الدخول"),
            (R(@"No VGM declared for (\S+)"), "لم يُصرَّح بالوزن المُتحقق (VGM) للحاوية $1"),
            (R(@"An export drop-off needs a booking number"), "تسليم حاوية الصادر يحتاج رقم حجز"),
            (R(@"An empty pickup needs a release / booking number"), "استلام الحاوية الفارغة يحتاج رقم إذن / حجز"),
            (R(@"\bShipping line hold\b"), "حجز الخط الملاحي"),
            (R(@"\bCustoms hold\b"), "حجز جمركي"),
            (R(@"\bTerminal hold\b"), "حجز من المحطة"),
            (R(@"\bUnpaid charges\b"), "رسوم غير مدفوعة"),
            (R(@"\bCargo hold\b"), "حجز على البضاعة"),
            (R(@"The terminal system could not be reached — this will be re-checked before arrival"), "تعذّر الوصول إلى نظام تشغيل المحطة — سيُعاد التحقق قبل الوصول"),

            // ---- the "ready to go" light
            (R(@"Missed — the truck did not arrive in its window"), "فات الموعد — لم تصل الشاحنة في نافذتها"),
            (R(@"Rejected at gate — contact the terminal"), "رُفضت عند البوابة — تواصل مع المحطة"),
            (R(@"In terminal — follow the yard instructions"), "داخل المحطة — اتبع تعليمات الساحة"),
            (R(@"Arrival window has passed — reschedule the appointment"), "انتهت نافذة الوصول — غيّر موعدك"),
            (R(@"Checks are still running"), "التحقق ما زال جارياً"),
            (R(@"Ready — proceed to the gate"), "جاهزة — توجّه إلى البوابة"),
            (R(@"Appointment (?<date>\w{3} \d\d \w{3} \d\d:\d\d) — gate opens for you at (?<at>\d\d:\d\d)"), "date:الموعد {date} — تفتح لك البوابة الساعة ${at}"),
            (R(@"^Visit completed$"), "اكتملت الزيارة"),

            // ---- the gate's decision
            (R(@"Container (\S+) was read but is not on appointment (\S+)\."), "قُرئت الحاوية $1 لكنها ليست في الموعد $2."),
            (R(@"Plate (\S+) was read but the appointment is for truck (\S+)\."), "قُرئت اللوحة $1 لكن الموعد للشاحنة $2."),
            (R(@"The QR code does not belong to any appointment at this terminal\."), "رمز QR لا يخص أي موعد في هذه المحطة."),
            (R(@"This terminal doesn't find appointments by their number: scan the driver's gate pass\."), "لا تبحث هذه المحطة عن المواعيد برقمها: امسح تصريح البوابة من تطبيق السائق."),
            (R(@"Appointment (\S+) was not found\."), "لم يُعثر على الموعد $1."),
            (R(@"No appointment found for this truck\."), "لا يوجد موعد لهذه الشاحنة."),
            (R(@"Appointment (\S+) is already inside the terminal\."), "الموعد $1 داخل المحطة بالفعل."),
            (R(@"Appointment (\S+) was already completed\."), "الموعد $1 مكتمل بالفعل."),
            (R(@"Appointment (\S+) is Cancelled\."), "الموعد $1 ملغى."),
            (R(@"Appointment (\S+) is NoShow\."), "الموعد $1: لم تحضر الشاحنة."),
            (R(@"Appointment (\S+) is RejectedAtGate\."), "الموعد $1 مرفوض عند البوابة."),
            (R(@"This truck is blocked at the terminal\."), "هذه الشاحنة محظورة في المحطة."),
            (R(@"This driver is blocked at the terminal\."), "هذا السائق محظور في المحطة."),
            (R(@"Appointment (\S+) names no driver: the terminal requires one\. Check the driver's ID and assign them\."), "الموعد $1 لا يحدد سائقاً والمحطة تشترط ذلك. تحقق من هوية السائق وعيّنه."),
            (R(@"The terminal requires the driver's gate pass from Yardsyncs mobile: check the driver's ID against the appointment\."), "تشترط المحطة تصريح البوابة من تطبيق Yardsyncs mobile: طابق هوية السائق مع الموعد."),
            (R(@"Late: the arrival window closed at (\d\d:\d\d)\."), "متأخر: أُغلقت نافذة الوصول الساعة $1."),
            (R(@"Early: the arrival window opens at (\d\d:\d\d)\."), "مبكر: تفتح نافذة الوصول الساعة $1."),
            (R(@"The terminal system could not be reached to re-check this truck — verify in the TOS and admit from the trouble desk\."), "تعذّر الوصول إلى نظام تشغيل المحطة لإعادة التحقق من هذه الشاحنة — تحقق في نظام التشغيل واسمح بالدخول من مكتب المشكلات."),
            (R(@"declared (\S+), read (\S+)"), "المُصرّح $1، المقروء $2"),
            (R(@"Seal doesn't match \((.+?)\)\. Check the seal before letting the truck in\."), "الختم غير مطابق ($1). افحص الختم قبل السماح بدخول الشاحنة."),
            (R(@"The gate pass code has expired — ask the driver to show the live code in the app\."), "انتهت صلاحية رمز تصريح البوابة — اطلب من السائق إظهار الرمز الحالي في التطبيق."),
            (R(@"The gate pass is no longer valid \(the driver signed in on another phone or it was revoked\)\."), "لم يعد تصريح البوابة صالحاً (سجّل السائق الدخول من هاتف آخر أو أُلغي)."),
            (R(@"The gate pass is valid, but appointment (\S+) is not this driver's\."), "تصريح البوابة صالح، لكن الموعد $1 ليس لهذا السائق."),
            (R(@"The gate pass could not be checked: expired, or not known at the terminal\."), "تعذّر التحقق من تصريح البوابة: منتهي الصلاحية أو غير معروف في المحطة."),
            (R(@"Not a Yardsyncs gate pass\."), "ليس تصريح بوابة من Yardsyncs."),
            (R(@"Rejected at gate: "), "رُفضت عند البوابة: "),
            (R(@"Admit — note: "), "سماح بالدخول — ملاحظة: "),
            (R(@"^Admit$"), "سماح بالدخول"),

            // ---- the automatic lanes
            (R(@"Nothing to identify the truck by\."), "لا يوجد ما يُعرّف الشاحنة."),
            (R(@"The barrier did not open"), "لم يُفتح الحاجز"),
            (R(@"Let the truck through by hand\."), "مرّر الشاحنة يدوياً."),
            (R(@"^Let in by a clerk\.$"), "سمح موظف بالدخول."),
            (R(@"^Turned away by a clerk\.$"), "ردّها موظف."),
            (R(@"Let out by a clerk: "), "سمح موظف بالخروج: "),

            // ---- booking
            (R(@"Item (\d+): "), "البند $1: "),
            (R(@"Don't set off: "), "لا تتحرك: "),
            (R(@"Container (.+?) already has a live appointment\."), "الحاوية $1 لها موعد قائم بالفعل."),
            (R(@"No capacity left in this slot for this booking\. Please choose another slot\."), "لا توجد سعة متبقية في هذه الفترة لهذا الحجز. اختر فترة أخرى."),
            (R(@"This slot is closed or has already ended\."), "هذه الفترة مغلقة أو انتهت."),
            (R(@"The new slot is closed or has already ended\."), "الفترة الجديدة مغلقة أو انتهت."),
            (R(@"The appointment is already in this slot\."), "الموعد في هذه الفترة بالفعل."),
            (R(@"The terminal system can't be reached right now, so the booking can't be checked\. Please try again in a few minutes\."), "تعذّر الوصول إلى نظام تشغيل المحطة الآن، فلا يمكن التحقق من الحجز. حاول مجدداً بعد دقائق."),
            (R(@"This truck already has (\d+) appointment\(s\) that day \(limit (\d+)\)\."), "لهذه الشاحنة $1 موعد في ذلك اليوم (الحد $2)."),
            (R(@"Cancellation closes (\d+) minutes before the slot\. You can still move the appointment to another slot\."), "يُغلق الإلغاء قبل الفترة بـ $1 دقيقة. ما زال بإمكانك نقل الموعد إلى فترة أخرى."),
            (R(@"The appointment changed in the meantime; reload and try again\."), "تغيّر الموعد في هذه الأثناء؛ أعد التحميل وحاول مجدداً."),
            (R(@"An appointment in status (\w+) cannot be changed\."), "لا يمكن تغيير موعد في الحالة $1."),
            (R(@"Truck (.+?) is blocked at this terminal\."), "الشاحنة $1 محظورة في هذه المحطة."),
            (R(@"Driver (.+?) is blocked at this terminal\."), "السائق $1 محظور في هذه المحطة."),
            (R(@"Cargo reference (\S+) is not known at this terminal\."), "مرجع البضاعة $1 غير معروف في هذه المحطة."),
            (R(@"Cargo (\S+) is assigned to another company\."), "البضاعة $1 مخصصة لشركة أخرى."),
            (R(@"No trips left to book on cargo (\S+)\."), "لا توجد رحلات متبقية للحجز على البضاعة $1."),
            (R(@"This user cannot book for this company\."), "لا يمكن لهذا المستخدم الحجز لهذه الشركة."),
            (R(@"This user cannot change this appointment\."), "لا يمكن لهذا المستخدم تغيير هذا الموعد."),

            // ---- the trouble desk
            (R(@"A note is required to admit, book or reject a truck\."), "يلزم كتابة ملاحظة للسماح بدخول الشاحنة أو حجزها أو ردّها."),
            (R(@"No open slot right now for a walk-in\."), "لا توجد فترة مفتوحة الآن لزيارة بدون موعد."),

            // ---- messages between the driver and the control room
            (R(@"Appointment (\S+) not found\."), "الموعد $1 غير موجود."),
            (R(@"Appointment (\S+) names no driver to write to\."), "الموعد $1 لا يحدد سائقاً لمراسلته."),
            (R(@"Appointment (\S+) is closed for messages\."), "الموعد $1 مغلق للرسائل."),
            (R(@"Write a message or choose a ready-made one\."), "اكتب رسالة أو اختر رسالة جاهزة."),
            (R(@"A message is at most (\d+) characters\."), "الرسالة $1 حرفاً على الأكثر."),
            (R(@"Send either a ready-made message or a text, not both\."), "أرسل رسالة جاهزة أو نصاً، لا الاثنين معاً."),
            (R(@"Only the driver shares a location\."), "السائق وحده يشارك الموقع."),
            (R(@"Send a location on its own\."), "أرسل الموقع وحده."),
            (R(@"The location is not valid\."), "الموقع غير صالح."),
            (R(@"Only terminal staff of this terminal can read and write drivers' messages\."), "قراءة رسائل السائقين والرد عليها لموظفي هذه المحطة فقط."),

            // ---- reports
            (R(@"Choose the company \(companyId\)\."), "اختر الشركة."),
            (R(@"Only terminal staff of this terminal and the company itself see its report\."), "يطّلع على تقرير الشركة موظفو هذه المحطة والشركة نفسها فقط."),
            (R(@"No access to this terminal\."), "لا صلاحية لك على هذه المحطة."),
            (R(@"Only terminal staff of this terminal see its KPIs\."), "مؤشرات أداء المحطة لموظفيها فقط."),
            (R(@"Only planners and terminal admins of this terminal choose who gets its reports\."), "يحدد مخططو المحطة ومسؤولوها فقط من يستلم تقاريرها."),
            (R(@"Give a valid email address\."), "أدخل بريداً إلكترونياً صالحاً."),
            (R(@"Reports are written in English \(en\) or Arabic \(ar\)\."), "تُكتب التقارير بالإنجليزية أو العربية."),
            (R(@"Choose the daily report, the weekly one or both\."), "اختر التقرير اليومي أو الأسبوعي أو كليهما."),
            (R(@"A terminal sends reports to at most (\d+) addresses\."), "ترسل المحطة التقارير إلى $1 عنواناً على الأكثر."),
            (R(@"Recipient (\d+) not found\."), "المستلم $1 غير موجود."),
            (R(@"'to' must be on or after 'from'\."), "يجب أن يكون تاريخ النهاية في تاريخ البداية أو بعده."),
            (R(@"The range can be at most (\d+) days\."), "يمكن أن تكون الفترة $1 يوماً على الأكثر."),
            (R(@"You can only act for your own company\."), "يمكنك التصرف لشركتك فقط."),

            // ---- signing in
            (R(@"Wrong email or password\."), "البريد الإلكتروني أو كلمة المرور غير صحيحة."),
            (R(@"Too many failed attempts\. Try again in a few minutes\."), "محاولات فاشلة كثيرة. حاول مجدداً بعد دقائق."),
            (R(@"Session expired\. Please sign in again\."), "انتهت الجلسة. سجّل الدخول من جديد."),
            (R(@"The sign-in took too long\. Enter your password again\."), "استغرق تسجيل الدخول وقتاً طويلاً. أدخل كلمة المرور من جديد."),
            (R(@"That code isn't right\. Check the phone's time and type the code the app shows now\."), "الرمز غير صحيح. تحقق من وقت الهاتف واكتب الرمز الذي يظهره التطبيق الآن."),
            (R(@"That code isn't right\."), "الرمز غير صحيح."),
            (R(@"The password or the code isn't right\."), "كلمة المرور أو الرمز غير صحيح."),
            (R(@"Admins must keep two-step sign-in\. To move to another phone, ask another admin to reset it\."), "يجب على المسؤولين إبقاء التحقق بخطوتين. للانتقال إلى هاتف آخر، اطلب من مسؤول آخر إعادة تعيينه."),
            (R(@"Set up two-step sign-in first: admins sign in with a code from an authenticator app\."), "أعدّ التحقق بخطوتين أولاً: يسجّل المسؤولون الدخول برمز من تطبيق مصادقة."),
            (R(@"This API key can only read\. Ask your company admin for a key that can book\."), "مفتاح API هذا للاطلاع فقط. اطلب من مسؤول شركتك مفتاحاً يمكنه الحجز."),
        ],
        // Urdu and Hindi: what drivers read (the ready-to-go reason, check results, the gate's answer). Other
        // sentences stay English for them.
        ["ur"] =
        [
            (R(@"Customs hold on (\S+)"), "$1 پر کسٹمز کی روک"),
            (R(@"Shipping line has not released (\S+)"), "شپنگ لائن نے $1 ریلیز نہیں کیا"),
            (R(@"Terminal hold on (\S+)"), "$1 پر ٹرمینل کی روک"),
            (R(@"Storage charges on (\S+) start today"), "$1 پر اسٹوریج چارجز آج سے شروع"),
            (R(@"Charges for (\S+) are unpaid"), "$1 کے چارجز ادا نہیں ہوئے"),
            (R(@"(\S+) is not available for pickup yet"), "$1 ابھی اٹھانے کے لیے دستیاب نہیں"),
            (R(@"Cargo (\S+) is not released by customs"), "کارگو $1 کسٹمز سے ریلیز نہیں ہوا"),
            (R(@"Cargo (\S+) is not released for delivery"), "کارگو $1 ڈیلیوری کے لیے ریلیز نہیں ہوا"),
            (R(@"Cut-off has passed for booking (\S+)"), "بکنگ $1 کا کٹ آف گزر چکا ہے"),
            (R(@"No VGM for (\S+) in N4 yet — it must be declared before gate-in"), "$1 کا VGM ابھی درج نہیں — گیٹ ان سے پہلے ضروری ہے"),
            (R(@"No VGM declared for (\S+)"), "$1 کا VGM درج نہیں"),
            (R(@"\bShipping line hold\b"), "شپنگ لائن کی روک"),
            (R(@"\bCustoms hold\b"), "کسٹمز کی روک"),
            (R(@"\bTerminal hold\b"), "ٹرمینل کی روک"),
            (R(@"\bUnpaid charges\b"), "غیر ادا شدہ چارجز"),
            (R(@"\bCargo hold\b"), "کارگو کی روک"),
            (R(@"The terminal system could not be reached — this will be re-checked before arrival"), "ٹرمینل سسٹم سے رابطہ نہیں ہو سکا — آمد سے پہلے دوبارہ چیک ہوگا"),
            (R(@"Missed — the truck did not arrive in its window"), "چھوٹ گیا — ٹرک اپنے وقت میں نہیں پہنچا"),
            (R(@"Rejected at gate — contact the terminal"), "گیٹ پر مسترد — ٹرمینل سے رابطہ کریں"),
            (R(@"In terminal — follow the yard instructions"), "ٹرمینل کے اندر — یارڈ کی ہدایات پر عمل کریں"),
            (R(@"Arrival window has passed — reschedule the appointment"), "آمد کا وقت گزر گیا — اپوائنٹمنٹ دوبارہ طے کریں"),
            (R(@"Checks are still running"), "جانچ ابھی جاری ہے"),
            (R(@"Ready — proceed to the gate"), "تیار — گیٹ کی طرف چلیں"),
            (R(@"Appointment (?<date>\w{3} \d\d \w{3} \d\d:\d\d) — gate opens for you at (?<at>\d\d:\d\d)"), "date:اپوائنٹمنٹ {date} — آپ کے لیے گیٹ ${at} بجے کھلے گا"),
            (R(@"^Visit completed$"), "دورہ مکمل"),
            (R(@"Rejected at gate: "), "گیٹ پر مسترد: "),
            (R(@"Late: the arrival window closed at (\d\d:\d\d)\."), "دیر: آمد کا وقت $1 بجے ختم ہو گیا۔"),
            (R(@"Early: the arrival window opens at (\d\d:\d\d)\."), "جلدی: آمد کا وقت $1 بجے شروع ہوگا۔"),
            (R(@"Item (\d+): "), "آئٹم $1: "),
            (R(@"Don't set off: "), "روانہ نہ ہوں: "),
            (R(@"Appointment (\S+) not found\."), "اپوائنٹمنٹ $1 نہیں ملی۔"),
            (R(@"Appointment (\S+) is closed for messages\."), "اپوائنٹمنٹ $1 پر اب پیغام نہیں بھیجے جا سکتے۔"),
            (R(@"Write a message or choose a ready-made one\."), "پیغام لکھیں یا تیار پیغام چنیں۔"),
            (R(@"A message is at most (\d+) characters\."), "پیغام زیادہ سے زیادہ $1 حروف کا ہو سکتا ہے۔"),
            (R(@"The location is not valid\."), "مقام درست نہیں۔"),
        ],
        ["hi"] =
        [
            (R(@"Customs hold on (\S+)"), "$1 पर कस्टम्स रोक"),
            (R(@"Shipping line has not released (\S+)"), "शिपिंग लाइन ने $1 रिलीज़ नहीं किया"),
            (R(@"Terminal hold on (\S+)"), "$1 पर टर्मिनल रोक"),
            (R(@"Storage charges on (\S+) start today"), "$1 पर स्टोरेज शुल्क आज से शुरू"),
            (R(@"Charges for (\S+) are unpaid"), "$1 का शुल्क बकाया है"),
            (R(@"(\S+) is not available for pickup yet"), "$1 अभी उठाने के लिए उपलब्ध नहीं"),
            (R(@"Cargo (\S+) is not released by customs"), "कार्गो $1 कस्टम्स से रिलीज़ नहीं हुआ"),
            (R(@"Cargo (\S+) is not released for delivery"), "कार्गो $1 डिलीवरी के लिए रिलीज़ नहीं हुआ"),
            (R(@"Cut-off has passed for booking (\S+)"), "बुकिंग $1 का कट-ऑफ़ बीत चुका है"),
            (R(@"No VGM for (\S+) in N4 yet — it must be declared before gate-in"), "$1 का VGM अभी दर्ज नहीं — गेट-इन से पहले ज़रूरी है"),
            (R(@"No VGM declared for (\S+)"), "$1 का VGM दर्ज नहीं"),
            (R(@"\bShipping line hold\b"), "शिपिंग लाइन रोक"),
            (R(@"\bCustoms hold\b"), "कस्टम्स रोक"),
            (R(@"\bTerminal hold\b"), "टर्मिनल रोक"),
            (R(@"\bUnpaid charges\b"), "बकाया शुल्क"),
            (R(@"\bCargo hold\b"), "कार्गो रोक"),
            (R(@"The terminal system could not be reached — this will be re-checked before arrival"), "टर्मिनल सिस्टम से संपर्क नहीं हो सका — पहुँचने से पहले फिर जाँच होगी"),
            (R(@"Missed — the truck did not arrive in its window"), "छूट गया — ट्रक अपने समय में नहीं पहुँचा"),
            (R(@"Rejected at gate — contact the terminal"), "गेट पर अस्वीकार — टर्मिनल से संपर्क करें"),
            (R(@"In terminal — follow the yard instructions"), "टर्मिनल के अंदर — यार्ड के निर्देशों का पालन करें"),
            (R(@"Arrival window has passed — reschedule the appointment"), "पहुँचने का समय बीत गया — अपॉइंटमेंट दोबारा तय करें"),
            (R(@"Checks are still running"), "जाँच अभी चल रही है"),
            (R(@"Ready — proceed to the gate"), "तैयार — गेट की ओर चलें"),
            (R(@"Appointment (?<date>\w{3} \d\d \w{3} \d\d:\d\d) — gate opens for you at (?<at>\d\d:\d\d)"), "date:अपॉइंटमेंट {date} — आपके लिए गेट ${at} बजे खुलेगा"),
            (R(@"^Visit completed$"), "यात्रा पूरी"),
            (R(@"Rejected at gate: "), "गेट पर अस्वीकार: "),
            (R(@"Late: the arrival window closed at (\d\d:\d\d)\."), "देर: पहुँचने का समय $1 बजे बंद हो गया।"),
            (R(@"Early: the arrival window opens at (\d\d:\d\d)\."), "जल्दी: पहुँचने का समय $1 बजे शुरू होगा।"),
            (R(@"Item (\d+): "), "आइटम $1: "),
            (R(@"Don't set off: "), "रवाना न हों: "),
            (R(@"Appointment (\S+) not found\."), "अपॉइंटमेंट $1 नहीं मिला।"),
            (R(@"Appointment (\S+) is closed for messages\."), "अपॉइंटमेंट $1 पर अब संदेश नहीं भेजे जा सकते।"),
            (R(@"Write a message or choose a ready-made one\."), "संदेश लिखें या तैयार संदेश चुनें।"),
            (R(@"A message is at most (\d+) characters\."), "संदेश अधिकतम $1 अक्षरों का हो सकता है।"),
            (R(@"The location is not valid\."), "स्थान मान्य नहीं है।"),
        ],
    };
}
