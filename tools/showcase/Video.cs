using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Editing = global::Windows.Media.Editing;
using Encoding = global::Windows.Media.MediaProperties;
using Speech = global::Windows.Media.SpeechSynthesis;
using Storage = global::Windows.Storage;
using Transcoding = global::Windows.Media.Transcoding;

namespace Pos.Showcase;

/// <summary>
/// The narrated tour: a slide for each thing the till and the owner's screen do, spoken and
/// captioned, put together into an MP4 a shop can be sent on WhatsApp.
/// </summary>
/// <remarks>
/// The pictures are the acceptance run's own screenshots of the release build, and the bills this
/// tool prints; the voice is one of Windows' own. Everything is done with what Windows has - no
/// video editor, no recording of the screen.
/// </remarks>
internal static class Video
{
    private const int Width = 1920;
    private const int Height = 1080;

    /// <summary>The frame round a screenshot taken of a real window, and the desktop below it.</summary>
    private static readonly Int32Rect WindowFrame = new(11, 11, 1920, 1008);

    private static readonly TimeSpan Lead = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan Tail = TimeSpan.FromMilliseconds(500);

    private static readonly Color Ink = Color.FromRgb(0xE8, 0xEE, 0xF6);
    private static readonly Color Muted = Color.FromRgb(0x9A, 0xA8, 0xBA);
    private static readonly Color Accent = Color.FromRgb(0x2F, 0xC7, 0xE8);

    private enum Kind
    {
        Screen,
        Paper,
        Card,
    }

    private sealed record Slide(Kind Kind, string Title, string Picture, string Say);

    /// <summary>What the tour shows and says, in order. "Control X" is spoken, and captioned as Ctrl+X.</summary>
    private static Slide[] Script(string shots, string showcase)
    {
        string Shot(string name) => Path.Combine(shots, name + ".png");
        string Till(string name) => Path.Combine(showcase, "till", name + ".png");
        string Bill(string name) => Path.Combine(showcase, "bills", name + ".png");

        return
        [
            new(Kind.Card, "RetailPOS", "Billing for provision stores and supermarkets",
                "This is RetailPOS, billing software for provision stores and supermarkets. It runs on one Windows computer, works without the internet, prints GST bills in Tamil or English, and every job has a key on the keyboard."),
            new(Kind.Screen, "The billing screen", Shot("till-01-startup"),
                "This is the billing screen. The cursor waits in the search box: scan a barcode, or type a few letters of a name. The keys along the bottom show what each one does, and F1 lists them all."),
            new(Kind.Screen, "Find any item", Shot("till-02-search"),
                "Typing part of a name lists every match. Arrow down to the one you want, and press Enter."),
            new(Kind.Screen, "The bill", Shot("till-04-bill"),
                "Each item goes on the bill with its MRP, the price before GST and the line total. F3 changes the quantity, F4 gives a discount, and Delete takes a line off."),
            new(Kind.Screen, "Tamil units", Till("units-grain"),
                "Sell in the measures customers ask for: aazhakku, padi and marakkaal for grain, veesai for jaggery, and a sack by the moottai. Part of one is allowed only where it makes sense."),
            new(Kind.Screen, "Flowers, fruit and greens", Till("units-flowers"),
                "Jasmine by the muzham, bananas by the seepu, greens by the kattu, sachets by the saram. Thirty-five traditional units, and pieces, kilos, litres and metres."),
            new(Kind.Paper, "The counter bill", Bill("counter-bill"),
                "The printed bill is laid out like a Tamil Nadu counter bill: the item, the quantity with its unit, and the amount, with the HSN code and GST rate under every item. The total is printed large, rounded to the rupee."),
            new(Kind.Screen, "Hold and recall", Shot("till-06-held"),
                "Customer gone back for something? F5 holds the bill and gives it a token. Serve the next customer, and F6 brings the held bill back exactly as it was."),
            new(Kind.Screen, "Payment", Shot("till-09-tender"),
                "F12 opens the payment. Cash, card, UPI, khata or loyalty points, or several together, and the till works out the change."),
            new(Kind.Screen, "UPI with the amount", Shot("till-10b-upi-slip"),
                "Pick UPI, and a QR code appears with the exact amount, ready for the customer's phone. Control Q prints it on a slip."),
            new(Kind.Screen, "Khata", Shot("till-12c-credit-owed"),
                "Regular customers can buy on khata and pay later. F8 takes a payment against what they owe, and Control K prints their statement."),
            new(Kind.Screen, "Returns", Shot("till-12e-return-picked"),
                "F9 takes goods back against the original bill. The till issues a GST credit note, and refunds in cash or takes it off the khata."),
            new(Kind.Screen, "Cash in and out", Shot("till-12i-expense-recorded"),
                "Control M records the opening float, expenses paid from the drawer, and cash put in or taken out, so the drawer always adds up."),
            new(Kind.Screen, "Phone and WhatsApp orders", Shot("till-12k-order-on-bill"),
                "Paste an order from WhatsApp with Control O. The till reads the items onto the bill, and keeps it until the customer comes."),
            new(Kind.Screen, "Closing the day", Shot("till-13-close-preview"),
                "At closing time, Shift F12 shows the day: the bills, the sales, and the cash that should be in the drawer. Press it again to close the day."),
            new(Kind.Paper, "The day-end report", Bill("day-end"),
                "The day-end report prints in Tamil or English, and checks that the sales, the tax and the payments all agree."),
            new(Kind.Screen, "The owner's screen", Shot("owner-02-figures"),
                "Control D opens the owner's screen, which can be kept behind a PIN. The figures show sales, cash and UPI, with charts of the takings day by day, by hour and by department."),
            new(Kind.Screen, "Stock", Shot("owner-03-stock"),
                "Stock shows what is on the shelf and what is running low, and a stock sheet counts everything in one go."),
            new(Kind.Screen, "The catalogue", Shot("owner-04c-catalogue-unit"),
                "Add items one at a time, with the HSN code and GST rate suggested from the name, or load the whole catalogue from a spreadsheet."),
            new(Kind.Screen, "Prices and shelf labels", Shot("owner-04e-labels-due"),
                "Change prices in bulk from a spreadsheet. Every item whose price changed waits for a new shelf label, printed with its barcode."),
            new(Kind.Screen, "Purchases", Shot("owner-14b-purchase-bill"),
                "Enter the supplier's bill as the goods arrive. The stock goes up, and the shop sees what it owes each supplier."),
            new(Kind.Screen, "Reorder", Shot("owner-15-orders"),
                "Reorder works out what to buy from each supplier from what sells each day, and copies the order to send."),
            new(Kind.Screen, "Use-by dates", Shot("owner-14e-near-its-date"),
                "It warns about goods near their use-by date, and lists what has not sold for weeks."),
            new(Kind.Screen, "Who owes what", Shot("owner-12-who-owes"),
                "Customers shows everyone on khata, what they owe, and for how long."),
            new(Kind.Screen, "The GST return", Shot("owner-13b-gst-this-month"),
                "Control 8 prepares the month's GST return for the accountant: sales by rate, the HSN summary and every bill number, saved as the files the GST tool reads."),
            new(Kind.Screen, "Hardware", Shot("owner-05b-hardware-drawn"),
                "Hardware tests the printer, the cash drawer and the scale, and shows a bill exactly as the printer will print it."),
            new(Kind.Screen, "Backups", Shot("owner-08-backup"),
                "Closing the day also backs up. Maintenance checks the data, and puts a backup back if it is ever needed."),
            new(Kind.Card, "RetailPOS", "Offline  ·  exact GST  ·  Tamil bills  ·  a key for every job",
                "RetailPOS. Billing that works without the internet, with exact GST, bills in Tamil, and a key for every job."),
        ];
    }

    public static void Make(string shots, string showcase)
    {
        var work = Directory.CreateDirectory(Path.Combine(showcase, "video")).FullName;
        var script = Script(shots, showcase);

        var pictures = new List<string>();

        for (var i = 0; i < script.Length; i++)
        {
            var path = Path.Combine(work, $"slide-{i + 1:D2}.png");
            Draw(script[i], i + 1, script.Length, path);
            pictures.Add(path);
            Console.WriteLine($"  slide {i + 1}: {script[i].Title}");
        }

        // The voice and the video are Windows Runtime work, done off the UI thread.
        Task.Run(async () =>
        {
            var voice = new Speech.SpeechSynthesizer();

            if (Speech.SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.DisplayName.Contains("Hazel", StringComparison.Ordinal)) is { } hazel)
                voice.Voice = hazel;

            voice.Options.SpeakingRate = 1.05;

            var slides = new List<(string Picture, string Voice, TimeSpan Length)>();

            for (var i = 0; i < script.Length; i++)
            {
                var wav = Path.Combine(work, $"voice-{i + 1:D2}.wav");
                slides.Add((pictures[i], wav, await Speak(voice, script[i].Say, wav)));
            }

            var total = slides.Aggregate(TimeSpan.Zero, (sum, s) => sum + Lead + s.Length + Tail);
            Console.WriteLine($"  narration: {total:m\\:ss}");

            // One to send on WhatsApp and keep, and one small enough to sit in a web page. The
            // pictures hold still for seconds at a time, so few frames and a low rate lose nothing.
            await Compose(slides, showcase, "RetailPOS-tour.mp4", Encoding.VideoEncodingQuality.HD1080p, 1_200_000, 15,
                Encoding.AudioEncodingProperties.CreateAac(48000, 2, 128000));
            await Compose(slides, showcase, "RetailPOS-tour-small.mp4", Encoding.VideoEncodingQuality.HD720p, 230_000, 10,
                Encoding.AudioEncodingProperties.CreateAac(44100, 1, 40000));
        }).GetAwaiter().GetResult();
    }

    // ---- The slides ---------------------------------------------------------------------------

    private static void Draw(Slide slide, int number, int count, string path)
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
                DrawCard(root, slide);
                break;
            case Kind.Paper:
                DrawPaper(root, slide, number, count);
                break;
            default:
                DrawScreen(root, slide, number, count);
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

    private static void DrawScreen(Grid root, Slide slide, int number, int count)
    {
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(Header(slide.Title, number, count));

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

        var caption = Caption(slide.Say, 30);
        caption.Margin = new Thickness(80, 6, 80, 34);
        Grid.SetRow(caption, 2);
        root.Children.Add(caption);
    }

    private static void DrawPaper(Grid root, Slide slide, int number, int count)
    {
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });

        var header = Header(slide.Title, number, count);
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

        var caption = Caption(slide.Say, 40);
        caption.Margin = new Thickness(40, 0, 110, 60);
        caption.VerticalAlignment = VerticalAlignment.Center;
        caption.TextAlignment = TextAlignment.Left;
        Grid.SetRow(caption, 1);
        Grid.SetColumn(caption, 1);
        root.Children.Add(caption);
    }

    private static void DrawCard(Grid root, Slide slide)
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
            Text = slide.Title,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 120,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Ink),
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        stack.Children.Add(new TextBlock
        {
            Text = slide.Picture,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 46,
            Foreground = new SolidColorBrush(Accent),
            Margin = new Thickness(0, 18, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        root.Children.Add(stack);
    }

    private static FrameworkElement Header(string title, int number, int count)
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
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 46,
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

    private static TextBlock Caption(string say, double size) => new()
    {
        Text = Captioned(say),
        FontFamily = new FontFamily("Segoe UI"),
        FontSize = size,
        LineHeight = size * 1.3,
        Foreground = new SolidColorBrush(Ink),
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
    };

    /// <summary>The spoken keys as they are written on the keyboard: "Control D" is Ctrl+D.</summary>
    private static string Captioned(string say) =>
        Regex.Replace(Regex.Replace(say, @"\bControl (\w+)", "Ctrl+$1"), @"\bShift (F\d+)", "Shift+$1");

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

    // ---- The voice and the video ----------------------------------------------------------------

    private static async Task<TimeSpan> Speak(Speech.SpeechSynthesizer voice, string text, string path)
    {
        using (var spoken = await voice.SynthesizeTextToStreamAsync(text))
        using (var file = File.Create(path))
            await spoken.AsStreamForRead().CopyToAsync(file);

        return WavLength(path);
    }

    /// <summary>How long a PCM WAV file plays: its data, at its byte rate.</summary>
    private static TimeSpan WavLength(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var byteRate = 0;
        var position = 12;

        while (position + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, position, 4);
            var size = BitConverter.ToInt32(bytes, position + 4);

            if (id == "fmt ")
                byteRate = BitConverter.ToInt32(bytes, position + 8 + 8);

            if (id == "data" && byteRate > 0)
                return TimeSpan.FromSeconds((double)size / byteRate);

            position += 8 + size + (size % 2);
        }

        throw new InvalidDataException($"{path} is not a WAV file with a data chunk.");
    }

    private static async Task Compose(
        List<(string Picture, string Voice, TimeSpan Length)> slides,
        string output,
        string name,
        Encoding.VideoEncodingQuality quality,
        uint videoBitrate,
        uint framesPerSecond,
        Encoding.AudioEncodingProperties audio)
    {
        var composition = new Editing.MediaComposition();
        var at = TimeSpan.Zero;

        foreach (var (picture, voice, length) in slides)
        {
            var shown = Lead + length + Tail;

            var image = await Storage.StorageFile.GetFileFromPathAsync(picture);
            composition.Clips.Add(await Editing.MediaClip.CreateFromImageFileAsync(image, shown));

            var spoken = await Storage.StorageFile.GetFileFromPathAsync(voice);
            var track = await Editing.BackgroundAudioTrack.CreateFromFileAsync(spoken);
            track.Delay = at + Lead;
            composition.BackgroundAudioTracks.Add(track);

            at += shown;
        }

        var profile = Encoding.MediaEncodingProfile.CreateMp4(quality);
        profile.Video.Bitrate = videoBitrate;
        profile.Video.FrameRate.Numerator = framesPerSecond;
        profile.Video.FrameRate.Denominator = 1;
        profile.Audio = audio;

        var folder = await Storage.StorageFolder.GetFolderFromPathAsync(output);
        var file = await folder.CreateFileAsync(name, Storage.CreationCollisionOption.ReplaceExisting);
        var result = await composition.RenderToFileAsync(file, Editing.MediaTrimmingPreference.Precise, profile);

        if (result != Transcoding.TranscodeFailureReason.None)
            throw new InvalidOperationException($"The video could not be made: {result}.");

        Console.WriteLine($"  {name}: {new FileInfo(file.Path).Length / 1048576.0:0.0} MB");
    }
}
