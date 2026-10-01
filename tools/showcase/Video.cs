using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Editing = global::Windows.Media.Editing;
using Encoding = global::Windows.Media.MediaProperties;
using Storage = global::Windows.Storage;
using Transcoding = global::Windows.Media.Transcoding;

namespace Pos.Showcase;

/// <summary>
/// The tour videos: the billing tour and the owner's tour, each in Tamil and in English, captioned in
/// its language and spoken by a person.
/// </summary>
/// <remarks>
/// <para>
/// The voice is a recording of somebody reading the script, one file a slide - a phone's voice
/// recorder will do - and each slide stays up for as long as its recording plays. There is no
/// computer voice: a shop is being asked to trust its books to this, and a synthetic voice reads as
/// a machine reading an advert. A slide with no recording yet is captioned and silent, held for as
/// long as it takes to read, so the video can be made and checked before anybody records.
/// </para>
/// <para>
/// The pictures are the acceptance run's screenshots of the release build and the bills this tool
/// prints; everything is put together with Windows' own media APIs.
/// </para>
/// </remarks>
internal static class Video
{
    private const int Width = 1920;
    private const int Height = 1080;

    /// <summary>The frame round a screenshot taken of a real window, and the desktop below it.</summary>
    private static readonly Int32Rect WindowFrame = new(11, 11, 1920, 1008);

    private static readonly TimeSpan Lead = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan Tail = TimeSpan.FromMilliseconds(600);

    private static readonly Color Ink = Color.FromRgb(0xE8, 0xEE, 0xF6);
    private static readonly Color Muted = Color.FromRgb(0x9A, 0xA8, 0xBA);
    private static readonly Color Accent = Color.FromRgb(0x2F, 0xC7, 0xE8);

    private static readonly string[] RecordingTypes = [".m4a", ".mp3", ".wav", ".aac", ".wma"];

    private enum Kind
    {
        Screen,
        Paper,
        Card,
    }

    private enum Language
    {
        Tamil,
        English,
    }

    /// <summary>One slide: its picture, and its title and words in each language.</summary>
    /// <param name="Picture">The picture's path; on a card, the line under the name, in English.</param>
    /// <param name="Below">On a card, the line under the name in Tamil. Unused otherwise.</param>
    private sealed record Slide(Kind Kind, string Picture, string TitleTa, string TitleEn, string SayTa, string SayEn, string Below = "")
    {
        public string Title(Language language) => language == Language.Tamil ? TitleTa : TitleEn;

        public string Say(Language language) => language == Language.Tamil ? SayTa : SayEn;

        public string CardLine(Language language) => language == Language.Tamil ? Below : Picture;
    }

    private sealed record Tour(string Key, string NameTa, string NameEn, Slide[] Slides);

    public static void Make(string shots, string showcase, string recordings)
    {
        Tour[] tours = [Billing(shots, showcase), Owner(shots, showcase)];

        WriteScript(tours, Path.Combine(showcase, "recording-script.html"));

        foreach (var tour in tours)
        {
            foreach (var language in new[] { Language.Tamil, Language.English })
                MakeOne(tour, language, showcase, recordings);
        }
    }

    private static string Code(Language language) => language == Language.Tamil ? "ta" : "en";

    private static void MakeOne(Tour tour, Language language, string showcase, string recordings)
    {
        var name = $"{tour.Key}-{Code(language)}";
        var work = Directory.CreateDirectory(Path.Combine(showcase, "video", name)).FullName;
        var spoken = Path.Combine(recordings, name);

        Console.WriteLine($"{name}:");

        var slides = new List<(string Picture, string? Voice, string Words)>();

        for (var i = 0; i < tour.Slides.Length; i++)
        {
            var slide = tour.Slides[i];
            var picture = Path.Combine(work, $"slide-{i + 1:D2}.png");
            Draw(slide, language, i + 1, tour.Slides.Length, picture);

            var voice = RecordingTypes
                .Select(type => Path.Combine(spoken, $"{i + 1:D2}{type}"))
                .FirstOrDefault(File.Exists);

            slides.Add((picture, voice, slide.Say(language)));
        }

        var recorded = slides.Count(s => s.Voice is not null);
        Console.WriteLine($"  {recorded} of {slides.Count} slides recorded{(recorded == 0 ? $" - none in {spoken}, so this one is captioned and silent" : string.Empty)}");

        // Windows Runtime work, done off the UI thread.
        Task.Run(async () =>
        {
            var timed = await Time(slides, language);
            WriteChapters(tour, language, timed, Path.Combine(showcase, "video", $"chapters-{name}.json"));

            // One to send on WhatsApp and keep, and one small enough to sit in a web page. The
            // pictures hold still for seconds at a time, so few frames and a low rate lose nothing.
            await Compose(timed, showcase, $"RetailPOS-{name}.mp4", Encoding.VideoEncodingQuality.HD1080p, 1_200_000, 15,
                Encoding.AudioEncodingProperties.CreateAac(48000, 2, 128000));
            await Compose(timed, showcase, $"RetailPOS-{name}-small.mp4", Encoding.VideoEncodingQuality.HD720p, 230_000, 10,
                Encoding.AudioEncodingProperties.CreateAac(44100, 1, 48000));
        }).GetAwaiter().GetResult();
    }

    // ---- What the tours show and say -----------------------------------------------------------

    private static Tour Billing(string shots, string showcase)
    {
        string Shot(string name) => Path.Combine(shots, name + ".png");
        string Till(string name) => Path.Combine(showcase, "till", name + ".png");
        string Bill(string name) => Path.Combine(showcase, "bills", name + ".png");

        return new("billing", "பில்லிங் சுற்றுலா", "The billing tour",
        [
            new(Kind.Card, "Billing for provision stores and supermarkets", "RetailPOS", "RetailPOS",
                "இது RetailPOS. மளிகைக் கடைகளுக்கும் சூப்பர் மார்க்கெட்டுகளுக்குமான பில்லிங் மென்பொருள். ஒரே கம்ப்யூட்டரில் ஓடும், இன்டர்நெட் தேவையில்லை. GST பில்லை தமிழிலோ ஆங்கிலத்திலோ அச்சிடும். ஒவ்வொரு வேலைக்கும் கீபோர்டில் ஒரு கீ உண்டு.",
                "This is RetailPOS, billing software for provision stores and supermarkets. It runs on one Windows computer, works without the internet, prints GST bills in Tamil or English, and every job has a key on the keyboard.",
                "மளிகை மற்றும் சூப்பர் மார்க்கெட் கடைகளுக்கான பில்லிங்"),
            new(Kind.Screen, Shot("till-01-startup"), "பில்லிங் திரை", "The billing screen",
                "இதுதான் பில்லிங் திரை. கர்சர் எப்போதும் தேடல் பெட்டியில் காத்திருக்கும். பார்கோடை ஸ்கேன் செய்யலாம், அல்லது பொருளின் பெயரில் சில எழுத்துக்களை டைப் செய்யலாம். கீழே உள்ள கீகள் ஒவ்வொன்றும் என்ன செய்யும் என்று காட்டும்; F1 அழுத்தினால் எல்லா கீகளும் தெரியும்.",
                "This is the billing screen. The cursor waits in the search box: scan a barcode, or type a few letters of a name. The keys along the bottom show what each one does, and F1 lists them all."),
            new(Kind.Screen, Shot("till-02-search"), "எந்தப் பொருளையும் தேடலாம்", "Find any item",
                "பெயரின் ஒரு பகுதியை டைப் செய்தால், பொருந்தும் பொருட்கள் எல்லாம் வரும். அம்புக்குறியால் வேண்டியதைத் தேர்ந்தெடுத்து Enter அழுத்துங்கள்.",
                "Typing part of a name lists every match. Arrow down to the one you want, and press Enter."),
            new(Kind.Screen, Shot("till-04-bill"), "பில்", "The bill",
                "ஒவ்வொரு பொருளும் MRP, GST-க்கு முந்தைய விலை, மொத்தத் தொகையுடன் பில்லில் வரும். F3 அளவை மாற்றும், F4 தள்ளுபடி தரும், Delete அந்த வரியை நீக்கும்.",
                "Each item goes on the bill with its MRP, the price before GST and the line total. F3 changes the quantity, F4 gives a discount, and Delete takes a line off."),
            new(Kind.Screen, Till("units-grain"), "நம் அளவைகள்", "Tamil units",
                "வாடிக்கையாளர் கேட்கும் அளவிலேயே விற்கலாம்: தானியத்துக்கு ஆழாக்கு, படி, மரக்கால்; வெல்லத்துக்கு வீசை; மூட்டையாகவும் விற்கலாம். அரைப் படி போன்ற பாதி அளவு, பொருத்தமான இடத்தில் மட்டுமே.",
                "Sell in the measures customers ask for: aazhakku, padi and marakkaal for grain, veesai for jaggery, and a sack by the moottai. Part of one is allowed only where it makes sense."),
            new(Kind.Screen, Till("units-flowers"), "பூ, பழம், கீரை", "Flowers, fruit and greens",
                "மல்லிகை முழத்தில், வாழைப்பழம் சீப்பில், கீரை கட்டில், ஷாம்பு பாக்கெட் சரத்தில். முப்பத்தைந்து பாரம்பரிய அளவைகளும், பீஸ், கிலோ, லிட்டர், மீட்டரும் உண்டு.",
                "Jasmine by the muzham, bananas by the seepu, greens by the kattu, sachets by the saram. Thirty-five traditional units, and pieces, kilos, litres and metres."),
            new(Kind.Paper, Bill("counter-bill"), "கவுண்டர் பில்", "The counter bill",
                "அச்சாகும் பில், நம் ஊர் கவுண்டர் பில் போலவே இருக்கும்: பொருள், அளவு, தொகை; ஒவ்வொரு பொருளின் கீழும் HSN கோடும் GST விகிதமும். மொத்தத் தொகை பெரிதாக, ரூபாய்க்கு முழுமையாக்கி அச்சாகும்.",
                "The printed bill is laid out like a Tamil Nadu counter bill: the item, the quantity with its unit, and the amount, with the HSN code and GST rate under every item. The total is printed large, rounded to the rupee."),
            new(Kind.Screen, Shot("till-06-held"), "பில்லை நிறுத்தி வைத்தல்", "Hold and recall",
                "வாடிக்கையாளர் வேறு பொருள் எடுக்கப் போயிருக்கிறாரா? F5 அழுத்தினால் பில் நிறுத்தி வைக்கப்படும். அடுத்தவருக்கு பில் போடுங்கள்; F6 அழுத்தினால் அந்த பில் அப்படியே திரும்ப வரும்.",
                "Customer gone back for something? F5 holds the bill and gives it a token. Serve the next customer, and F6 brings the held bill back exactly as it was."),
            new(Kind.Screen, Shot("till-09-tender"), "பணம் பெறுதல்", "Payment",
                "F12 அழுத்தினால் பணம் பெறும் பகுதி திறக்கும். ரொக்கம், கார்டு, UPI, கடன், புள்ளிகள், அல்லது பலவும் சேர்த்து. மீதம் எவ்வளவு என்று அதுவே கணக்கிடும்.",
                "F12 opens the payment. Cash, card, UPI, khata or loyalty points, or several together, and the till works out the change."),
            new(Kind.Screen, Shot("till-10b-upi-slip"), "தொகையுடன் UPI", "UPI with the amount",
                "UPI தேர்ந்தெடுத்தால், சரியான தொகையுடன் QR கோடு திரையில் வரும்; வாடிக்கையாளர் போனில் ஸ்கேன் செய்யலாம். Ctrl+Q அழுத்தினால் அதைச் சீட்டில் அச்சிடும்.",
                "Pick UPI, and a QR code appears with the exact amount, ready for the customer's phone. Ctrl+Q prints it on a slip."),
            new(Kind.Screen, Shot("till-12c-credit-owed"), "கடன் கணக்கு", "Khata",
                "தெரிந்த வாடிக்கையாளர்கள் கடனில் வாங்கி பிறகு கொடுக்கலாம். F8 அழுத்தி அவர்கள் கடனுக்குப் பணம் வாங்கலாம்; Ctrl+K அவர்களின் கணக்கு அறிக்கையை அச்சிடும்.",
                "Regular customers can buy on khata and pay later. F8 takes a payment against what they owe, and Ctrl+K prints their statement."),
            new(Kind.Screen, Shot("till-12e-return-picked"), "திருப்பி வாங்குதல்", "Returns",
                "F9 அழுத்தி, அசல் பில்லின்படி பொருட்களைத் திருப்பி வாங்கலாம். GST கிரெடிட் நோட் தானே வரும்; பணமாகத் திருப்பித் தரலாம், அல்லது கடனில் கழிக்கலாம்.",
                "F9 takes goods back against the original bill. The till issues a GST credit note, and refunds in cash or takes it off the khata."),
            new(Kind.Screen, Shot("till-12i-expense-recorded"), "பணம் வைத்தல், எடுத்தல்", "Cash in and out",
                "Ctrl+M மூலம் காலைச் சில்லறை, பெட்டியிலிருந்து செலவு, பணம் வைத்தது, எடுத்தது எல்லாம் பதிவாகும். அதனால் பணப்பெட்டி கணக்கு எப்போதும் சரியாக இருக்கும்.",
                "Ctrl+M records the opening float, expenses paid from the drawer, and cash put in or taken out, so the drawer always adds up."),
            new(Kind.Screen, Shot("till-12k-order-on-bill"), "போன், வாட்ஸ்அப் ஆர்டர்", "Phone and WhatsApp orders",
                "வாட்ஸ்அப்பில் வந்த ஆர்டரை Ctrl+O அழுத்தி ஒட்டுங்கள். பொருட்கள் தானே பில்லில் ஏறும்; வாடிக்கையாளர் வரும் வரை காத்திருக்கும்.",
                "Paste an order from WhatsApp with Ctrl+O. The till reads the items onto the bill, and keeps it until the customer comes."),
            new(Kind.Screen, Shot("till-13-close-preview"), "நாள் முடிவு", "Closing the day",
                "கடை மூடும் நேரத்தில் Shift+F12 அழுத்தினால் அன்றைய கணக்கு தெரியும்: பில்கள், விற்பனை, பெட்டியில் இருக்க வேண்டிய பணம். மீண்டும் அழுத்தினால் நாள் முடியும்.",
                "At closing time, Shift+F12 shows the day: the bills, the sales, and the cash that should be in the drawer. Press it again to close the day."),
            new(Kind.Paper, Bill("day-end"), "நாள் இறுதி அறிக்கை", "The day-end report",
                "நாள் இறுதி அறிக்கை தமிழிலோ ஆங்கிலத்திலோ அச்சாகும். விற்பனை, வரி, பணம் மூன்றும் ஒத்துப்போகிறதா என்றும் சரிபார்க்கும்.",
                "The day-end report prints in Tamil or English, and checks that the sales, the tax and the payments all agree."),
            new(Kind.Card, "Offline  ·  exact GST  ·  Tamil bills  ·  a key for every job", "RetailPOS", "RetailPOS",
                "RetailPOS. இன்டர்நெட் இல்லாமலே பில்லிங், சரியான GST, தமிழில் பில், ஒவ்வொரு வேலைக்கும் ஒரு கீ.",
                "RetailPOS. Billing that works without the internet, with exact GST, bills in Tamil, and a key for every job.",
                "இன்டர்நெட் இல்லாமல்  ·  சரியான GST  ·  தமிழ் பில்  ·  ஒவ்வொரு வேலைக்கும் ஒரு கீ"),
        ]);
    }

    private static Tour Owner(string shots, string showcase)
    {
        string Shot(string name) => Path.Combine(shots, name + ".png");
        string Bill(string name) => Path.Combine(showcase, "bills", name + ".png");

        return new("owner", "உரிமையாளர் சுற்றுலா", "The owner's tour",
        [
            new(Kind.Card, "For the shop owner", "RetailPOS", "RetailPOS",
                "இது RetailPOS-இன் உரிமையாளர் பகுதி. கடையின் விற்பனை, லாபம், ஸ்டாக், கடன், GST எல்லாம் ஒரே திரையில், இன்டர்நெட் இல்லாமலே.",
                "This is the owner's side of RetailPOS: the shop's takings, profit, stock, khata and GST, all on one screen, and all of it without the internet.",
                "கடை உரிமையாளருக்கு"),
            new(Kind.Screen, Shot("owner-01-pin"), "PIN பாதுகாப்பு", "Behind a PIN",
                "பில்லிங் திரையில் Ctrl+D அழுத்தினால் உரிமையாளர் திரை திறக்கும். அதற்கு PIN வைத்தால், பில் போடுபவர்கள் கணக்குகளைப் பார்க்க முடியாது.",
                "From the billing screen, Ctrl+D opens the owner's screen. Put a PIN on it, and the billing staff cannot see the figures."),
            new(Kind.Screen, Shot("owner-02-figures"), "கடையின் கணக்கு", "The figures",
                "ஏழு, முப்பது, தொண்ணூறு நாட்களின் நிகர விற்பனை, இன்றைய வசூல், பெட்டியில் இருக்க வேண்டிய ரொக்கம், கார்டு மற்றும் UPI மூலம் வங்கிக்கு வர வேண்டியது எல்லாம் இங்கே தெரியும்.",
                "The figures show net sales for seven, thirty or ninety days, today's takings, the cash that should be in the drawer, and what should reach the bank from card and UPI."),
            new(Kind.Screen, Shot("owner-02-figures-p2"), "கடைக்குக் கிடைத்த லாபம்", "What the shop earned",
                "அடக்க விலை உள்ள ஒவ்வொரு பொருளின் லாபமும் லாப சதவீதமும் தெரியும்; கடை நடத்த ஆன டீ, ஸ்நாக்ஸ் போன்ற செலவுகளைக் கழித்த பின் மீதம் என்ன என்றும் தெரியும்.",
                "What the shop earned: profit and margin on every item with a cost price, and what is left after the costs of running the shop, like tea and snacks."),
            new(Kind.Screen, Shot("owner-02-figures-p3"), "நன்றாக விற்பவை, தேங்குபவை", "Stars and dead weight",
                "ஒவ்வொரு பொருளும் எவ்வளவு வேகமாக விற்கிறது, எவ்வளவு லாபம் தருகிறது என்று வரைபடத்தில் தெரியும்: நட்சத்திரங்கள், மறைந்த முத்துக்கள், அதிகம் விற்பவை, தேங்கி நிற்பவை.",
                "Every item placed by how fast it sells and what it earns: the stars, the hidden gems, the volume drivers, and the dead weight."),
            new(Kind.Screen, Shot("owner-02-figures-p4"), "கடை எப்போது பிஸி", "When the shop is busy",
                "எந்த நேரத்தில் கடை பிஸியாக இருக்கிறது என்று மணி வாரியாகவும், வாரம் முழுவதற்கும் தெரியும். அமைதியான நேரத்தில் சரக்கு இறக்கலாம், ஸ்டாக் எண்ணலாம்.",
                "When the shop is busy, hour by hour, and the whole week at a glance. The quiet hours are the time for a delivery or a stock count."),
            new(Kind.Screen, Shot("owner-02-figures-p5"), "பணம் எப்படி வந்தது", "How customers paid",
                "வாடிக்கையாளர்கள் ரொக்கம், UPI, கார்டு, கடன் என எப்படிப் பணம் கொடுத்தார்கள், எந்தப் பிரிவிலிருந்து விற்பனை வந்தது என்றும் தெரியும்.",
                "How customers paid, in cash, UPI, card or khata, and which departments the takings came from."),
            new(Kind.Screen, Shot("owner-03-stock"), "ஸ்டாக்", "Stock",
                "அலமாரியில் என்ன இருக்கிறது, எது குறைந்து வருகிறது என்று ஸ்டாக் பகுதி காட்டும். நீங்கள் முடிவு செய்யும் அளவுக்குக் கீழே போனால் எச்சரிக்கும்.",
                "Stock shows what is on the shelf and what is running low, below its reorder level, or down to a share of full that you choose."),
            new(Kind.Screen, Shot("owner-03b-stock-sheet-confirm"), "மொத்தமாக எண்ணுதல்", "Count in bulk",
                "கடை முழுவதையும் Excel ஸ்டாக் ஷீட்டில் எண்ணி, திரும்ப ஏற்றலாம். எதையும் மாற்றும் முன், என்ன மாறும் என்று காட்டும்.",
                "Count the whole shop on a stock sheet in Excel, and load it back. Every change is shown before anything is written."),
            new(Kind.Screen, Shot("owner-14e-near-its-date"), "காலாவதி தேதி", "Use-by dates",
                "காலாவதி தேதி நெருங்கும் பொருட்களை எச்சரிக்கும்; வாரக்கணக்கில் விற்காதவற்றையும் பட்டியலிடும். அலமாரியில் பணம் தூங்காது.",
                "It warns about goods near their use-by date, and lists what has not sold for weeks, so money is not left sitting on the shelf."),
            new(Kind.Screen, Shot("owner-04b-catalogue-hsn"), "புதிய பொருள் சேர்த்தல்", "Adding an item",
                "புதிய பொருள் சேர்க்கும்போது, பெயரை வைத்தே HSN கோடையும் GST விகிதத்தையும் அதுவே பரிந்துரைக்கும். அல்லது முழுப் பட்டியலையும் ஸ்ப்ரெட்ஷீட்டில் இருந்து ஏற்றலாம்; ஏற்றும் முன் சரிபார்க்கும்.",
                "Add an item, and it suggests the HSN code and GST rate from the name. Or load the whole catalogue from a spreadsheet; it is checked before anything is written."),
            new(Kind.Screen, Shot("owner-04c-catalogue-unit"), "அளவைகள்", "Units",
                "ஒவ்வொரு பொருளும் எந்த அளவில் விற்கப்படும் என்று தேர்ந்தெடுங்கள்: பீஸ், கிலோ, லிட்டர், அல்லது படி, முழம், சீப்பு, கட்டு போன்ற நம் அளவைகள்.",
                "Choose how each item is sold: pieces, kilos and litres, or the traditional units, padi, muzham, seepu, kattu and thirty more."),
            new(Kind.Screen, Shot("owner-04e-labels-due"), "விலை மாற்றம்", "Prices and offers",
                "விலைகளை ஒரே ஷீட்டில் மொத்தமாக மாற்றலாம்; விலை மாறிய பொருட்களுக்குப் புதிய விலைச் சீட்டு காத்திருக்கும். இரண்டு வாங்கினால் ஒன்று இலவசம் போன்ற ஆஃபர்களும் உண்டு.",
                "Change prices in bulk from a price sheet; every item whose price changed waits for a new shelf label. Offers too, like buy two get one free."),
            new(Kind.Paper, Bill("shelf-labels"), "விலைச் சீட்டுகள்", "Shelf labels",
                "ஒவ்வொரு சீட்டிலும் MRP, உங்கள் விலை, சேமிப்பு எல்லாம் தமிழிலோ ஆங்கிலத்திலோ இருக்கும்; பில் போடும்போது ஸ்கேன் செய்ய பார்கோடும் உண்டு.",
                "Each label shows the MRP, your price and the saving, per unit, in Tamil or English, with a barcode the till can scan."),
            new(Kind.Screen, Shot("owner-14b-purchase-bill"), "கொள்முதல்", "Purchases",
                "சரக்கு வந்தவுடன் சப்ளையர் பில்லைப் பதிவு செய்யுங்கள். ஸ்டாக் தானே கூடும்; ஒவ்வொரு சப்ளையருக்கும் எவ்வளவு பாக்கி, எவ்வளவு கொடுத்தீர்கள் என்று தெரியும்.",
                "Enter the supplier's bill as the goods arrive. The stock goes up, and you see what you owe each supplier and what you have paid."),
            new(Kind.Screen, Shot("owner-15-orders"), "மறு ஆர்டர்", "Reorder",
                "கடந்த நான்கு வாரங்களில் விற்றதை வைத்து, ஒவ்வொரு சப்ளையரிடமும் என்ன வாங்க வேண்டும் என்று கணக்கிட்டு, அனுப்ப ஒரு மெசேஜாகவும் தயார் செய்யும்.",
                "Reorder works out what to buy from each supplier, from what sold over the last four weeks, and copies the order as a message to send."),
            new(Kind.Screen, Shot("owner-12-who-owes"), "யார் எவ்வளவு கடன்", "Who owes what",
                "கடன் வாங்கிய ஒவ்வொருவரும் எவ்வளவு, எத்தனை நாளாக என்று தெரியும். பழைய கடனை முதலில் வசூலிக்கலாம்.",
                "Customers shows everyone on khata, what they owe, and for how long, so the oldest khata is chased first."),
            new(Kind.Paper, Bill("khata-statement"), "கடன் கணக்கு அறிக்கை", "The khata statement",
                "ஒவ்வொரு வாடிக்கையாளருக்கும் கணக்கு அறிக்கை அச்சிடலாம்: எல்லா பில்களும், கொடுத்த பணமும், பாக்கித் தொகைக்கான UPI கோடுடன்.",
                "Print a customer's statement, with every bill and payment, and a UPI code for exactly what they owe."),
            new(Kind.Screen, Shot("owner-13b-gst-this-month"), "GST ரிட்டர்ன்", "The GST return",
                "Ctrl+8 மாதாந்திர GST ரிட்டர்னைத் தயார் செய்யும்: விகித வாரியான விற்பனை, HSN சுருக்கம், வழங்கிய எல்லா பில் எண்களும்.",
                "Ctrl+8 prepares the month's GST return: sales by rate, the HSN summary in the unit each thing was sold in, and every bill number issued."),
            new(Kind.Screen, Shot("owner-13c-gst-saved"), "ஆடிட்டருக்கு", "For the accountant",
                "உங்கள் ஆடிட்டருக்குப் படிக்க ஒரு பக்கமாகவும், GST ஆஃப்லைன் டூல் படிக்கும் ஃபைல்களாகவும் சேமிக்கலாம். தாக்கல் செய்யும் முன் சரிபார்க்க வேண்டியவற்றையும் சுட்டிக்காட்டும்.",
                "Save it for your accountant as a page to read and as the files the GST offline tool reads. Notes before filing point out anything to check first."),
            new(Kind.Paper, Bill("day-end"), "நாள் இறுதி அறிக்கை", "The day-end report",
                "தினமும் மாலை, நாள் இறுதி அறிக்கை பெட்டியில் இருக்க வேண்டிய ரொக்கம், விற்பனை, வரி, கடன் வசூல் எல்லாவற்றையும் காட்டி, எல்லாம் ஒத்துப்போகிறதா என்றும் சரிபார்க்கும்.",
                "Every evening, the day-end report shows the cash expected in the drawer, the sales, the tax and the khata collected, and checks that they all agree."),
            new(Kind.Screen, Shot("owner-08-backup"), "பேக்கப்", "Backups",
                "நாளை முடிக்கும்போதே கடைக் கணக்குகள் பேக்கப் ஆகும். கம்ப்யூட்டர் பழுதானால், மெயின்டனன்ஸ் பகுதியில் இருந்து பேக்கப்பைத் திரும்பக் கொண்டு வரலாம்.",
                "Closing the day also backs up the shop's books. If the computer ever fails, Maintenance puts a backup back."),
            new(Kind.Screen, Shot("owner-06b-settings-compact"), "அமைப்புகள்", "Settings",
                "அமைப்புகளில் பில்லின் வடிவம், ஸ்டாக் எப்போது குறைவு என்று கணக்கிடுவது, கடையின் UPI ID, PIN ஆகியவற்றை அமைக்கலாம்.",
                "Settings choose the bill's layout, when stock counts as low, the shop's UPI ID, and the PIN."),
            new(Kind.Screen, Shot("owner-05b-hardware-drawn"), "பிரிண்டர், பணப்பெட்டி, தராசு", "Hardware",
                "பிரிண்டர், பணப்பெட்டி, எடைத்தராசு மூன்றையும் சோதிக்கலாம்; பிரிண்டர் அச்சிடுவது போலவே பில்லைத் திரையில் பார்க்கலாம்.",
                "Hardware tests the printer, the cash drawer and the weighing scale, and shows a bill exactly as the printer will print it."),
            new(Kind.Card, "Your shop's books, on your own computer", "RetailPOS", "RetailPOS",
                "RetailPOS. உங்கள் கடையின் விற்பனை, ஸ்டாக், கடன், GST எல்லாம், உங்கள் கம்ப்யூட்டரிலேயே, தமிழிலோ ஆங்கிலத்திலோ.",
                "RetailPOS. Your shop's takings, stock, khata and GST, on your own computer, in Tamil or English.",
                "உங்கள் கடைக் கணக்கு, உங்கள் கம்ப்யூட்டரிலேயே"),
        ]);
    }

    // ---- The script, for whoever records it ------------------------------------------------------

    /// <summary>
    /// Every line to record, numbered, with the name to save it under: one page to read from while
    /// recording, on a phone or on paper.
    /// </summary>
    private static void WriteScript(Tour[] tours, string path)
    {
        var page = new StringBuilder();
        page.Append("""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Tour Narration Script</title>
            <style>
            body{font:18px/1.6 "Nirmala UI","Segoe UI",system-ui,sans-serif;max-width:760px;margin:0 auto;padding:24px 16px 80px;color:#132033;background:#f5f7f9}
            h1{font-size:1.9rem;margin:0 0 6px} h2{margin:48px 0 6px;font-size:1.5rem} h3{margin:6px 0 18px;color:#0b7f9c;font-size:1rem;font-family:Consolas,monospace}
            ol{padding-left:0;list-style:none} li{background:#fff;border:1px solid #dbe3ea;border-radius:12px;padding:14px 16px;margin-bottom:12px}
            .file{font:600 .85rem Consolas,monospace;color:#0b7f9c} .title{font-weight:700} .say{font-size:1.25rem;margin-top:6px}
            .how{background:#fff;border:1px solid #dbe3ea;border-radius:12px;padding:6px 20px}
            </style></head><body>
            <h1>Tour narration script</h1>
            <p>One recording a line, read naturally, the way you would explain the shop's billing to a friend.</p>
            <div class="how"><ol style="list-style:decimal;padding-left:1.2em">
            <li style="border:0;padding:4px 0;margin:0">Record in a quiet room with the phone about a hand's width from your mouth. The phone's own voice recorder is fine.</li>
            <li style="border:0;padding:4px 0;margin:0">One file for each numbered line. Leave a second of quiet before and after.</li>
            <li style="border:0;padding:4px 0;margin:0">Save each file under the name shown, for example <b>01.m4a</b>, in the folder shown above each list.</li>
            <li style="border:0;padding:4px 0;margin:0">The slide stays on screen for as long as your recording plays, so there is no need to hurry.</li>
            <li style="border:0;padding:4px 0;margin:0">Keys like F12 and Ctrl+Q are read as "F twelve" and "control Q".</li>
            </ol></div>
            """);

        foreach (var tour in tours)
        {
            foreach (var language in new[] { Language.Tamil, Language.English })
            {
                var folder = $"tools\\showcase\\recordings\\{tour.Key}-{Code(language)}";
                page.Append($"<h2>{Encode(language == Language.Tamil ? tour.NameTa : tour.NameEn)} &middot; {(language == Language.Tamil ? "தமிழ்" : "English")}</h2>");
                page.Append($"<h3>{Encode(folder)}</h3><ol>");

                for (var i = 0; i < tour.Slides.Length; i++)
                {
                    var slide = tour.Slides[i];
                    page.Append($"<li><div class=\"file\">{i + 1:D2}.m4a</div><div class=\"title\">{Encode(slide.Title(language))}</div><div class=\"say\">{Encode(slide.Say(language))}</div></li>");
                }

                page.Append("</ol>");
            }
        }

        page.Append("</body></html>");
        File.WriteAllText(path, page.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"  {Path.GetFileName(path)}");
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    // ---- The slides ---------------------------------------------------------------------------

    private static void Draw(Slide slide, Language language, int number, int count, string path)
    {
        var root = new Grid
        {
            Width = Width,
            Height = Height,
            Background = new LinearGradientBrush(Color.FromRgb(0x0E, 0x14, 0x20), Color.FromRgb(0x06, 0x09, 0x0F), 90),
        };

        switch (slide.Kind)
        {
            case Kind.Card:
                DrawCard(root, slide, language);
                break;
            case Kind.Paper:
                DrawPaper(root, slide, language, number, count);
                break;
            default:
                DrawScreen(root, slide, language, number, count);
                break;
        }

        root.Measure(new Size(Width, Height));
        root.Arrange(new Rect(0, 0, Width, Height));
        root.UpdateLayout();

        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var file = File.Create(path);
        encoder.Save(file);
    }

    /// <summary>Tamil in the Windows face made for it; English in Segoe UI.</summary>
    private static FontFamily Face(Language language) =>
        new(language == Language.Tamil ? "Nirmala UI" : "Segoe UI");

    private static void DrawScreen(Grid root, Slide slide, Language language, int number, int count)
    {
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(Header(slide.Title(language), language, number, count));

        var picture = new Border
        {
            Margin = new Thickness(56, 8, 56, 8),
            CornerRadius = new CornerRadius(14),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, Accent.R, Accent.G, Accent.B)),
            BorderThickness = new Thickness(2),
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new Image { Source = Load(slide.Picture), Stretch = Stretch.Uniform },
        };

        Grid.SetRow(picture, 1);
        root.Children.Add(picture);

        var caption = Caption(slide.Say(language), language, language == Language.Tamil ? 27 : 30);
        caption.Margin = new Thickness(80, 6, 80, 34);
        Grid.SetRow(caption, 2);
        root.Children.Add(caption);
    }

    private static void DrawPaper(Grid root, Slide slide, Language language, int number, int count)
    {
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });

        var header = Header(slide.Title(language), language, number, count);
        Grid.SetColumnSpan(header, 2);
        root.Children.Add(header);

        // The bill on white paper, as the customer holds it.
        var paper = new Border
        {
            Margin = new Thickness(80, 10, 30, 50),
            Padding = new Thickness(18),
            Background = Brushes.White,
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = new Image { Source = Load(slide.Picture), Stretch = Stretch.Uniform },
        };

        Grid.SetRow(paper, 1);
        root.Children.Add(paper);

        var caption = Caption(slide.Say(language), language, language == Language.Tamil ? 36 : 40);
        caption.Margin = new Thickness(40, 0, 110, 60);
        caption.VerticalAlignment = VerticalAlignment.Center;
        caption.TextAlignment = TextAlignment.Left;
        Grid.SetRow(caption, 1);
        Grid.SetColumn(caption, 1);
        root.Children.Add(caption);
    }

    private static void DrawCard(Grid root, Slide slide, Language language)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };

        stack.Children.Add(new Border
        {
            Width = 120,
            Height = 120,
            CornerRadius = new CornerRadius(28),
            Background = new LinearGradientBrush(Color.FromRgb(0x2F, 0xC7, 0xE8), Color.FromRgb(0x16, 0xB8, 0x86), 45),
            Margin = new Thickness(0, 0, 0, 36),
            Child = new TextBlock
            {
                Text = "₹",
                FontSize = 72,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });

        stack.Children.Add(new TextBlock
        {
            Text = "RetailPOS",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 120,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Ink),
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        stack.Children.Add(new TextBlock
        {
            Text = slide.CardLine(language),
            FontFamily = Face(language),
            FontSize = language == Language.Tamil ? 40 : 46,
            Foreground = new SolidColorBrush(Accent),
            Margin = new Thickness(0, 18, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        root.Children.Add(stack);
    }

    private static FrameworkElement Header(string title, Language language, int number, int count)
    {
        var header = new Grid { Margin = new Thickness(56, 30, 56, 14) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(new TextBlock
        {
            Text = "RETAILPOS",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Accent),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 6, 22, 0),
        });
        left.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = Face(language),
            FontSize = language == Language.Tamil ? 42 : 46,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Ink),
        });

        header.Children.Add(left);

        var counter = new TextBlock
        {
            Text = $"{number} / {count}",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 24,
            Foreground = new SolidColorBrush(Muted),
            VerticalAlignment = VerticalAlignment.Center,
        };

        Grid.SetColumn(counter, 1);
        header.Children.Add(counter);
        return header;
    }

    private static TextBlock Caption(string say, Language language, double size) => new()
    {
        Text = say,
        FontFamily = Face(language),
        FontSize = size,
        LineHeight = size * 1.35,
        Foreground = new SolidColorBrush(Ink),
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
    };

    private static BitmapSource Load(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(path);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();

        // A screenshot of a real window: the frame and the desktop under it go.
        if (image.PixelWidth == 1942 && image.PixelHeight == 1030)
        {
            var cropped = new CroppedBitmap(image, WindowFrame);
            cropped.Freeze();
            return cropped;
        }

        return image;
    }

    // ---- Timing, chapters and the video -------------------------------------------------------------

    private sealed record Timed(string Picture, string? Voice, TimeSpan Length);

    /// <summary>
    /// How long each slide stays up: as long as its recording, or, with none, as long as its caption
    /// takes to read.
    /// </summary>
    private static async Task<List<Timed>> Time(List<(string Picture, string? Voice, string Words)> slides, Language language)
    {
        var timed = new List<Timed>();

        foreach (var (picture, voice, words) in slides)
        {
            TimeSpan length;

            if (voice is not null)
            {
                var file = await Storage.StorageFile.GetFileFromPathAsync(voice);
                var track = await Editing.BackgroundAudioTrack.CreateFromFileAsync(file);
                length = track.OriginalDuration;
            }
            else
            {
                // Tamil words are longer to read than English ones.
                var count = words.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                var seconds = count / (language == Language.Tamil ? 1.6 : 2.4) + 1.5;
                length = TimeSpan.FromSeconds(Math.Max(5, seconds));
            }

            timed.Add(new Timed(picture, voice, length));
        }

        return timed;
    }

    /// <summary>Where each slide starts, for the page to jump to.</summary>
    private static void WriteChapters(Tour tour, Language language, List<Timed> timed, string path)
    {
        var at = TimeSpan.Zero;
        var chapters = new List<object>();

        for (var i = 0; i < timed.Count; i++)
        {
            chapters.Add(new { at = Math.Round(at.TotalSeconds, 1), title = tour.Slides[i].Title(language) });
            at += Lead + timed[i].Length + Tail;
        }

        File.WriteAllText(path, JsonSerializer.Serialize(chapters, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }), new UTF8Encoding(false));

        Console.WriteLine($"  {at:m\\:ss} long");
    }

    private static async Task Compose(
        List<Timed> slides,
        string output,
        string name,
        Encoding.VideoEncodingQuality quality,
        uint videoBitrate,
        uint framesPerSecond,
        Encoding.AudioEncodingProperties audio)
    {
        var composition = new Editing.MediaComposition();
        var at = TimeSpan.Zero;

        foreach (var slide in slides)
        {
            var shown = Lead + slide.Length + Tail;

            var image = await Storage.StorageFile.GetFileFromPathAsync(slide.Picture);
            composition.Clips.Add(await Editing.MediaClip.CreateFromImageFileAsync(image, shown));

            if (slide.Voice is not null)
            {
                var spoken = await Storage.StorageFile.GetFileFromPathAsync(slide.Voice);
                var track = await Editing.BackgroundAudioTrack.CreateFromFileAsync(spoken);
                track.Delay = at + Lead;
                composition.BackgroundAudioTracks.Add(track);
            }

            at += shown;
        }

        var profile = Encoding.MediaEncodingProfile.CreateMp4(quality);
        profile.Video.Bitrate = videoBitrate;
        profile.Video.FrameRate.Numerator = framesPerSecond;
        profile.Video.FrameRate.Denominator = 1;
        profile.Audio = composition.BackgroundAudioTracks.Count > 0 ? audio : null;

        var folder = await Storage.StorageFolder.GetFolderFromPathAsync(output);
        var file = await folder.CreateFileAsync(name, Storage.CreationCollisionOption.ReplaceExisting);
        var result = await composition.RenderToFileAsync(file, Editing.MediaTrimmingPreference.Precise, profile);

        if (result != Transcoding.TranscodeFailureReason.None)
            throw new InvalidOperationException($"{name} could not be made: {result}.");

        Console.WriteLine($"  {name}: {new FileInfo(file.Path).Length / 1048576.0:0.0} MB");
    }
}
