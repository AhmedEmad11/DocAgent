namespace DocAgent.IntegrationTests;

/// <summary>
/// A small bilingual corpus for measuring retrieval quality. Each paragraph becomes (roughly) one chunk when the
/// test shrinks the chunk size. A question's <c>Marker</c> is a substring that occurs only in the paragraph holding the answer.
/// </summary>
public static class RetrievalCorpus
{
    public sealed record CorpusDocument(string FileName, string[] Paragraphs);

    public sealed record CorpusQuestion(string Question, string Marker, bool ExactTerm = false);

    public static CorpusDocument[] Documents => [.. Core, .. Distractors()];

    /// <summary>
    /// Many near-identical paragraphs that differ only in a code or a number. They make the exact-term questions
    /// realistic: the embedding of "INV-2026-0417" looks a lot like that of any other invoice number.
    /// </summary>
    private static CorpusDocument[] Distractors()
    {
        var random = new Random(20261112);
        string[] services = ["data migration workshop", "security audit", "training course", "website redesign", "quarterly review", "cloud setup"];
        string[] months = ["January", "February", "March", "April", "May", "June", "July"];
        string[] components = ["power supply", "memory module", "network card", "cooling fan", "storage controller", "display panel"];
        string[] arabicServices = ["الشحن والتخزين", "الصيانة الدورية", "التدريب", "الاستشارات الإدارية", "التوريد", "التركيب"];
        string[] arabicCompanies = ["مؤسسة النور", "شركة الفجر", "مؤسسة البناء", "شركة الأفق", "مؤسسة السلام", "شركة المستقبل"];
        string[] arabicParts = ["المضخة", "الفلتر الثاني", "المستشعر", "لوحة التحكم", "الصمام", "المروحة"];
        int[] taken = [417, 5043, 2207, 2291, 9931, 7712, 55012, 4417, 48213];
        int Number(int max)
        {
            int n;
            do { n = random.Next(1000, max); } while (taken.Contains(n));
            return n;
        }

        string[] Make(int count, Func<string> paragraph) => [.. Enumerable.Range(0, count).Select(_ => paragraph())];

        return
        [
            new("invoice-log.txt", Make(24, () =>
                $"Invoice INV-2026-{Number(9999):0000} covers the {services[random.Next(services.Length)]} delivered in {months[random.Next(months.Length)]} and amounts to {random.Next(2, 40) * 500:N0} Canadian dollars excluding tax.")),
            new("error-codes.txt", Make(24, () =>
                $"If the display shows ERR-{Number(9999):0000} the {components[random.Next(components.Length)]} reported a fault. Switch the device off, wait {random.Next(2, 9)} minutes and start it again before calling support.")),
            new("model-catalog.txt", Make(24, () =>
                $"The model KX-{Number(9999):0000}-B is covered by a limited warranty of {random.Next(2, 6) * 6} months from the date of purchase and is delivered with a quick start guide.")),
            new("فواتير.txt", Make(24, () =>
                $"الفاتورة رقم {Number(99999)} صادرة لصالح {arabicCompanies[random.Next(arabicCompanies.Length)]} بمبلغ {random.Next(3, 50) * 1000} ريال مقابل خدمات {arabicServices[random.Next(arabicServices.Length)]}.")),
            new("رموز-الأخطاء.txt", Make(24, () =>
                $"عند ظهور الرمز E-{Number(9999):0000} على الشاشة فهذا يعني وجود عطل في {arabicParts[random.Next(arabicParts.Length)]}. أوقف الجهاز لمدة {random.Next(2, 9)} دقائق ثم أعد تشغيله.")),
            new("نماذج-الموارد.txt", Make(24, () =>
                $"تقدم الطلبات عبر النموذج HR-{Number(9999):0000} قبل الموعد بأسبوع على الأقل، ويجب أن يوافق عليها المدير المباشر.")),
        ];
    }

    public static readonly CorpusDocument[] Core =
    [
        new("handbook.txt",
        [
            "Employee Handbook. Welcome to the company. This handbook describes how we work together, what you can expect from your manager and what we expect from you during your first year.",
            "Full-time employees receive 25 days of paid vacation per calendar year. Unused vacation days can be carried over until the end of March of the following year, after which they expire without compensation.",
            "New parents are entitled to 16 weeks of paid parental leave. The leave can be taken in one block or split into two periods within the first twelve months after the birth or adoption of the child.",
            "Staff who work from home may claim a one-time equipment allowance by submitting form EQ-2207 to the facilities team. The allowance covers a desk, a chair and a monitor up to the approved limit.",
            "Travel expense reports must be submitted within 30 days of returning from a trip. Receipts are required for every item above fifty dollars, and late reports may be rejected by the finance department.",
            "If your security badge is lost or stolen, call the front desk on extension 4417 immediately so that it can be deactivated. A replacement badge is issued the same day after identity verification.",
        ]),
        new("router-manual.txt",
        [
            "NetStream router user manual. This manual explains how to install, configure and troubleshoot your wireless router at home or in a small office.",
            "If the display shows ERR-5043 the firmware checksum does not match. Hold the reset button for ten seconds until the power light flashes, then upload the firmware file again from the support website.",
            "A slowly blinking amber light means that the router has power but cannot reach the internet provider. Check the cable between the router and the wall socket and restart the modem before calling support.",
            "Firmware version 3.8.2 was released in March and improves the stability of the guest network. To update, open the administration page, choose System, then Firmware, and select the downloaded file.",
            "The administration page is available at 192.168.7.1 using any web browser. The default user name is admin; change the password during the first login to protect the network from strangers.",
            "The model KX-2291-B is covered by a limited warranty of 24 months from the date of purchase. The warranty does not cover damage caused by lightning, flooding or unauthorized repairs.",
        ]),
        new("consulting-agreement.txt",
        [
            "Consulting agreement between Birchwood Analytics and the client. This agreement sets out the services, fees and responsibilities of both parties for the duration of the project.",
            "The client shall pay each invoice within 45 days of receipt. Payments made after the due date are subject to a late fee of 1.5 percent per month on the outstanding balance until the invoice is settled.",
            "Either party may terminate this agreement by giving 90 days written notice to the other party. Work completed up to the termination date must still be paid in full by the client.",
            "This agreement is governed by the laws of the Province of Ontario, Canada. Any dispute that cannot be settled by negotiation shall be submitted to the courts of Toronto.",
            "Invoice INV-2026-0417 covers the data migration workshop delivered in April and amounts to 12,400 Canadian dollars excluding tax. It was issued to the client on the first of May.",
            "All reports and software produced during the project remain the property of the client once paid, except for pre-existing tools of the consultant, which are licensed under a separate agreement.",
        ]),
        new("trial-summary.txt",
        [
            "Clinical study summary. This document summarises the design, participants and main findings of a randomised trial of a new medication for chronic migraine.",
            "The principal investigator of the study was Dr. Halvorsen of the university hospital. The study was registered under the identifier NCT-0048213 and enrolled 312 participants across six centres.",
            "Participants received a dose of 40 mg twice daily for twelve weeks. The control group received a placebo tablet that looked identical, and neither patients nor doctors knew who received which tablet.",
            "The most frequent side effect was headache, reported by 12 percent of patients in the treatment group. Nausea and dizziness were less common, and no serious adverse events were attributed to the drug.",
            "The monthly number of migraine days fell by an average of 4.2 days in the treatment group, compared with 1.9 days in the placebo group, a difference that was statistically significant.",
        ]),
        new("sourdough.txt",
        [
            "Weekend sourdough bread. A simple recipe for a crusty loaf with an open crumb, made with flour, water, salt and an active starter.",
            "Mix 500 g of flour with 375 g of water for a hydration of 75 percent. Let the dough rest for thirty minutes, then add 10 g of salt and 100 g of bubbly starter and knead gently until combined.",
            "After shaping, place the loaf in a floured basket and leave it in the refrigerator overnight for about 12 hours. The cold rise develops flavour and makes the dough easier to score.",
            "Preheat the oven with a heavy Dutch pot to 230 degrees Celsius. Bake the loaf covered for twenty minutes, then remove the lid and bake for another twenty minutes until deep golden brown.",
            "For a spicier loaf, knead in two teaspoons of crushed Zanzibar pepper together with the salt. The pepper adds a warm, citrus-like aroma that goes well with cheese.",
        ]),
        new("سياسة-الإجازات.txt",
        [
            "سياسة الإجازات في الشركة. توضح هذه الوثيقة أنواع الإجازات المتاحة للموظفين وكيفية طلبها والموافقة عليها.",
            "يحصل كل موظف بدوام كامل على ثلاثين يوما من الإجازة السنوية المدفوعة. يمكن ترحيل الأيام غير المستخدمة إلى السنة التالية بحد أقصى خمسة عشر يوما.",
            "تقدم طلبات الإجازة عبر النموذج HR-9931 قبل موعد الإجازة بأسبوعين على الأقل، ويجب أن يوافق عليها المدير المباشر قبل السفر.",
            "تمنح الموظفة بعد الولادة إجازة أمومة مدتها سبعون يوما بأجر كامل، ويحق لها طلب ساعات عمل مخفضة خلال الأشهر الستة الأولى بعد العودة.",
            "في حالة المرض يجب إبلاغ المدير في صباح اليوم نفسه وتقديم تقرير طبي إذا زادت مدة الغياب عن ثلاثة أيام متتالية.",
        ]),
        new("دليل-الجهاز.txt",
        [
            "دليل استخدام جهاز تنقية المياه المنزلي. يشرح هذا الدليل طريقة التركيب والتشغيل والصيانة الدورية للجهاز.",
            "عند ظهور الرمز E-7712 على الشاشة فهذا يعني أن الفلتر الأول مسدود. أوقف الجهاز، واستبدل الفلتر، ثم اضغط على زر الإعادة لمدة خمس ثوان.",
            "يجب تغيير فلتر الكربون كل ستة أشهر، وتغيير الغشاء كل سنتين، للحفاظ على جودة المياه وتجنب الروائح غير المرغوبة.",
            "يعمل الجهاز بضغط مياه يتراوح بين ثلاثة وستة بار. إذا كان الضغط منخفضا فيجب تركيب مضخة تقوية قبل مدخل الجهاز.",
            "مدة الضمان على الجهاز ثلاث سنوات من تاريخ الشراء، ولا يشمل الضمان الأعطال الناتجة عن سوء الاستخدام أو التركيب غير الصحيح.",
        ]),
        new("تقرير-مالي.txt",
        [
            "التقرير المالي السنوي لشركة النخيل للتجارة. يعرض هذا التقرير النتائج المالية للشركة خلال السنة الماضية وخططها للسنة القادمة.",
            "بلغت إيرادات الشركة هذا العام أربعة ملايين وخمسمائة ألف ريال، بزيادة قدرها ثمانية بالمئة مقارنة بالعام السابق.",
            "تم افتتاح فرع جديد في مدينة الدمام في شهر مارس، وبلغت تكلفة التجهيز مليونا ومئتي ألف ريال.",
            "الفاتورة رقم 55012 صادرة لصالح مؤسسة الواحة بمبلغ ثمانية عشر ألف ريال مقابل خدمات الشحن والتخزين.",
            "تخطط الشركة لتوظيف أربعين موظفا جديدا في قسم المبيعات خلال الربع الأول، وإطلاق متجر إلكتروني بحلول شهر سبتمبر.",
        ]),
    ];

    public static readonly CorpusQuestion[] Questions =
    [
        // English, paraphrased (semantic)
        new("How many vacation days do employees get each year?", "25 days of paid vacation"),
        new("How long is maternity or paternity leave?", "16 weeks of paid parental leave"),
        new("What should I do if my router shows a checksum problem?", "firmware checksum does not match"),
        new("What does a slowly blinking orange light on the router mean?", "slowly blinking amber"),
        new("How long do I have to pay an invoice?", "within 45 days of receipt"),
        new("What interest is charged when a payment is late?", "late fee of 1.5 percent"),
        new("Which court handles disputes under the consulting contract?", "courts of Toronto"),
        new("How often did patients report headaches as a side effect?", "reported by 12 percent"),
        new("How long should the bread dough rest in the fridge?", "refrigerator overnight for about 12 hours"),
        new("At what temperature do I bake the loaf?", "230 degrees Celsius"),
        // English, exact terms
        new("ERR-5043", "ERR-5043", true),
        new("form EQ-2207", "EQ-2207", true),
        new("Who is Dr. Halvorsen?", "Dr. Halvorsen", true),
        new("What is invoice INV-2026-0417 for?", "INV-2026-0417", true),
        new("warranty for KX-2291-B", "KX-2291-B", true),
        new("What is NCT-0048213?", "NCT-0048213", true),
        new("Zanzibar pepper", "Zanzibar pepper", true),
        new("extension 4417", "extension 4417", true),
        // Arabic, paraphrased
        new("كم يوما من الإجازة السنوية يحصل عليها الموظف؟", "ثلاثين يوما من الإجازة السنوية"),
        new("ما مدة إجازة الأمومة؟", "إجازة أمومة مدتها سبعون يوما"),
        new("كم سنة مدة الضمان على جهاز تنقية المياه؟", "مدة الضمان على الجهاز ثلاث سنوات"),
        new("متى يجب تغيير فلتر الكربون؟", "فلتر الكربون كل ستة أشهر"),
        new("كم بلغت إيرادات الشركة هذا العام؟", "أربعة ملايين وخمسمائة ألف ريال"),
        new("متى سيتم إطلاق المتجر الإلكتروني؟", "متجر إلكتروني بحلول شهر سبتمبر"),
        // Arabic, exact terms
        new("HR-9931", "HR-9931", true),
        new("الرمز E-7712", "E-7712", true),
        new("الفاتورة رقم 55012", "55012", true),
        new("مؤسسة الواحة", "مؤسسة الواحة", true),
    ];

    /// <summary>Questions the corpus cannot answer: the top hit should score below a sensible MinScore.</summary>
    public static readonly string[] Unanswerable =
    [
        "Who won the 2018 football World Cup?",
        "What is the capital city of Australia?",
        "How do I reset my Netflix password?",
        "What is the company policy on bringing pets to the office?",
        "How many floors does the head office building have?",
        "What is the recommended dose of ibuprofen for children?",
        "من هو مخترع الهاتف؟",
        "ما هو سعر صرف الدولار اليوم؟",
        "كيف أطبخ الكبسة؟",
        "كم عدد موظفي الشركة في الرياض؟",
    ];
}
