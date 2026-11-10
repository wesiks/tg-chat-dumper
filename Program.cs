using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;
using Spectre.Console.Rendering;
using TL;
using WTelegram;

namespace TgChatDumper;

public sealed class DumperConfig
{
    [JsonPropertyName("api_id")]
    public int ApiId { get; set; } = 0;

    [JsonPropertyName("api_hash")]
    public string ApiHash { get; set; } = "";

    [JsonPropertyName("sessions_folder")]
    public string SessionsFolder { get; set; } = "sessions";

    [JsonPropertyName("output_folder")]
    public string OutputFolder { get; set; } = "dumps";

    [JsonPropertyName("download_media")]
    public bool DownloadMedia { get; set; } = true;

    [JsonPropertyName("skip_existing")]
    public bool SkipExisting { get; set; } = true;

    [JsonPropertyName("max_file_size_mb")]
    public int MaxFileSizeMb { get; set; } = 0;

    [JsonPropertyName("proxy_url")]
    public string? ProxyUrl { get; set; } = null;
}

public sealed class DumpedMessageRecord
{
    public int Id { get; set; }
    public DateTime DateUtc { get; set; }
    public long SenderId { get; set; }
    public string SenderName { get; set; } = "";
    public int? ReplyToMsgId { get; set; }
    public string Text { get; set; } = "";
    public string? MediaCategory { get; set; }
    public string? MediaFileName { get; set; }
    public long? MediaSizeBytes { get; set; }
    public bool IsProtectedOriginal { get; set; }
}

public sealed class LiveStats
{
    public string ChatTitle { get; set; } = "";
    public long ChatId { get; set; }
    public string ProtectionStatus { get; set; } = "";
    public string AccountName { get; set; } = "";
    public string OutputDir { get; set; } = "";

    public int MessagesCrawled { get; set; }
    public int PhotosSaved { get; set; }
    public int VideosSaved { get; set; }
    public int AudioSaved { get; set; }
    public int VoiceSaved { get; set; }
    public int RoundVideoSaved { get; set; }
    public int DocsSaved { get; set; }
    public int StickersSaved { get; set; }
    public long TotalBytesDownloaded { get; set; }

    public string CurrentAction { get; set; } = "Инициализация...";
    public string CurrentFileName { get; set; } = "";
    public long CurrentFileBytes { get; set; }
    public long CurrentFileTotal { get; set; }
    public double CurrentSpeedBytesPerSec { get; set; }

    public IRenderable BuildRenderable(string proxyInfo)
    {
        var root = new Table().Border(TableBorder.None).HideHeaders().Expand();
        root.AddColumn("Content");

        var rule = new Rule("[bold deepskyblue1]Telegram Dumper — Активная выгрузка данных и медиа[/]")
        {
            Justification = Justify.Center
        };
        rule.RuleStyle("deepskyblue1");
        root.AddRow(rule);

        var metaGrid = new Grid();
        metaGrid.AddColumn();
        metaGrid.AddColumn();
        metaGrid.AddRow(
            $"[grey]Чат:[/]         [white]{Markup.Escape(ChatTitle)}[/] [grey](ID: {ChatId})[/]",
            $"[grey]Аккаунт:[/]     [white]{Markup.Escape(AccountName)}[/]"
        );
        metaGrid.AddRow(
            $"[grey]Режим защиты:[/] {ProtectionStatus}",
            $"[grey]Туннель:[/]     [cyan]{Markup.Escape(proxyInfo)}[/]"
        );
        metaGrid.AddRow(
            $"[grey]Папка дампа:[/]  [underline grey]{Markup.Escape(OutputDir)}[/]",
            $"[grey]Статус:[/]      [yellow]{Markup.Escape(CurrentAction)}[/]"
        );

        root.AddRow(metaGrid);
        root.AddRow(new Markup("\n"));
        root.AddRow(new Markup("[bold white]Текущее состояние компонентов выгрузки[/]").Centered());

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey37).Expand();
        table.AddColumn(new TableColumn("[bold grey]#[/]").Centered().Width(4));
        table.AddColumn(new TableColumn("[bold white]Компонент[/]").Width(32));
        table.AddColumn(new TableColumn("[bold white]Статус[/]").Width(26));
        table.AddColumn(new TableColumn("[bold white]Описание[/]"));

        table.AddRow(
            "[grey]1[/]",
            "История сообщений (текст)",
            $"[bold green]√ {MessagesCrawled:N0} выгружено[/]",
            "Чтение сообщений с сервера Telegram и экспорт в transcript/JSON"
        );
        table.AddRow(
            "[grey]2[/]",
            "Фотографии (макс. качество)",
            $"[cyan]√ {PhotosSaved} сохранено[/]",
            "Сохранение изображений в оригинальном разрешении (media/photos)"
        );
        table.AddRow(
            "[grey]3[/]",
            "Видео и круглые видеосообщения",
            $"[magenta]√ {VideosSaved + RoundVideoSaved} сохранено[/]",
            "Видеофайлы .mp4 и видеозаметки (media/videos, media/round_video)"
        );
        table.AddRow(
            "[grey]4[/]",
            "Голосовые заметки и музыка",
            $"[yellow]√ {AudioSaved + VoiceSaved} сохранено[/]",
            "Голосовые аудиосообщения .ogg и музыкальные треки .mp3 (media/voice)"
        );
        table.AddRow(
            "[grey]5[/]",
            "Документы, архивы и файлы",
            $"[blue]√ {DocsSaved} сохранено[/]",
            "Все прикрепленные файлы, PDF, документы без сжатия (media/documents)"
        );
        table.AddRow(
            "[grey]6[/]",
            "Общий объем сохраненных данных",
            $"[bold gold1]√ {FormatBytes(TotalBytesDownloaded)}[/]",
            "Суммарный размер всех загруженных медиа-файлов на локальном диске"
        );

        string curStatus;
        string curDesc;
        if (CurrentFileTotal > 0 && CurrentFileBytes > 0)
        {
            double pct = Math.Clamp((double)CurrentFileBytes / CurrentFileTotal, 0.0, 1.0);
            string speed = CurrentSpeedBytesPerSec > 0 ? $"{FormatBytes((long)CurrentSpeedBytesPerSec)}/s" : "";
            curStatus = $"[yellow]{pct * 100:0.0}% ({speed})[/]";
            curDesc = $"[white]{Markup.Escape(CurrentFileName)}[/] [grey]({FormatBytes(CurrentFileBytes)} / {FormatBytes(CurrentFileTotal)})[/]";
        }
        else
        {
            curStatus = "[grey]Ожидание пакетов[/]";
            curDesc = Markup.Escape(CurrentAction);
        }

        table.AddRow("[grey]7[/]", "Активный поток передачи", curStatus, curDesc);
        root.AddRow(table);

        if (CurrentFileTotal > 0 && CurrentFileBytes > 0)
        {
            double pct = Math.Clamp((double)CurrentFileBytes / CurrentFileTotal, 0.0, 1.0);
            int barWidth = 36;
            int filled = (int)(pct * barWidth);
            string bar = new string('█', filled) + new string('░', barWidth - filled);
            root.AddRow(new Markup($"\n[deepskyblue1]{bar}[/] [bold]{pct * 100:0.0}%[/] [grey]({FormatBytes(CurrentFileBytes)} / {FormatBytes(CurrentFileTotal)})[/]"));
        }

        root.AddRow(new Markup("\n[bold cyan]Текущее действие:[/] [white]" + Markup.Escape(CurrentAction) + "[/] [grey][[Ctrl+C для безопасной остановки]][/]\n"));
        return root;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < units.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {units[order]}";
    }
}

public static class Program
{
    private const string ConfigFileName = "config.json";
    private const int DefaultApiId = 2040;
    private const string DefaultApiHash = "b18441a1ff607e10a989891a5462e627";
    private static DumperConfig _config = new();
    private static readonly CancellationTokenSource Cts = new();

    public static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Cts.Cancel();
            AnsiConsole.MarkupLine("\n[bold red][[!] Остановка по запросу пользователя. Буферы сохраняются на диск...[/]");
        };

        Helpers.Log = (lvl, str) => { };

        _config = LoadOrCreateConfig();
        EnsureApiCredentials(_config);
        EnsureProxyConfig(_config);

        Directory.CreateDirectory(_config.SessionsFolder);
        Directory.CreateDirectory(_config.OutputFolder);

        string sessionPath = SelectOrCreateSession(_config);

        using var client = new Client(ConfigCallback(sessionPath));

        if (!string.IsNullOrWhiteSpace(_config.ProxyUrl))
        {
            client.MTProxyUrl = _config.ProxyUrl;
        }

        PrintHeader("Telegram Dumper — Авторизация MTProto");
        AnsiConsole.MarkupLine("[deepskyblue1][[*]] Установка соединения с Telegram через туннель...[/]");

        User myUser;
        try
        {
            myUser = await client.LoginUserIfNeeded();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[bold red][[-]] Ошибка авторизации: {Markup.Escape(ex.Message)}[/]");
            return;
        }

        myUser = client.User;
        if (myUser == null)
        {
            AnsiConsole.MarkupLine("[bold red][[-]] Не удалось получить объект пользователя Telegram.[/]");
            return;
        }

        string userDisplay = $"{myUser.first_name} {myUser.last_name}".Trim();
        if (!string.IsNullOrEmpty(myUser.username)) userDisplay += $" (@{myUser.username})";
        userDisplay += $" (ID: {myUser.id})";

        Messages_DialogsBase dialogsData = null!;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("[deepskyblue1]Загрузка списка диалогов и чатов с сервера...[/]", async ctx =>
            {
                dialogsData = await ExecuteWithFloodRetry(async () => await client.Messages_GetAllDialogs(), "GetAllDialogs");
            });

        var dialogList = dialogsData.Dialogs
            .Select(d =>
            {
                var peer = dialogsData.UserOrChat(d);
                return new { Dialog = d, Peer = peer };
            })
            .Where(x => x.Peer != null)
            .ToList();

        if (dialogList.Count == 0)
        {
            AnsiConsole.MarkupLine("[bold yellow][[!] В данном аккаунте нет доступных чатов.[/]");
            return;
        }

        var allChats = dialogList.Select(x => x.Peer).ToList();
        IPeerInfo? targetChat = allChats.FirstOrDefault();

        while (!Cts.IsCancellationRequested)
        {
            PrintHeader("BetterDumper — Извлечение переписки и медиа Telegram", userDisplay);

            AnsiConsole.Write(new Markup("[bold white]Текущее состояние компонентов и параметров выгрузки[/]\n").Centered());

            var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey37).Expand();
            table.AddColumn(new TableColumn("[bold grey]#[/]").Centered().Width(4));
            table.AddColumn(new TableColumn("[bold white]Компонент[/]").Width(34));
            table.AddColumn(new TableColumn("[bold white]Статус[/]").Width(26));
            table.AddColumn(new TableColumn("[bold white]Описание[/]"));

            string targetChatName = targetChat != null ? GetPeerTitle(targetChat) : "Не выбран";
            bool isProtected = targetChat != null && IsChatNoForwards(targetChat);
            string protStatus = isProtected
                ? "[bold red]! Защищен (Обход включен)[/]"
                : "[bold green]√ Чисто (Обычный чат)[/]";

            table.AddRow(
                "[grey]1[/]",
                "Целевой чат / канал для дампа",
                $"[white]{Markup.Escape(Truncate(targetChatName, 22))}[/]",
                targetChat != null ? $"ID: {targetChat.ID} {(!string.IsNullOrEmpty(targetChat.MainUsername) ? "(@" + targetChat.MainUsername + ")" : "")}" : "Выберите чат из меню ниже"
            );
            table.AddRow(
                "[grey]2[/]",
                "Режим обхода защиты (noforwards)",
                protStatus,
                isProtected
                    ? "В чате включен запрет на копирование и сохранение. Прямой MTProto-обход активен."
                    : "Ограничений на сохранение в чате нет. Прямой стриминг файлов включен."
            );
            table.AddRow(
                "[grey]3[/]",
                "Выкачка всех медиа-файлов",
                _config.DownloadMedia ? "[bold green]√ Включено (Все типы)[/]" : "[bold red]Отключено (Только текст)[/]",
                _config.DownloadMedia
                    ? "Скачивает фото, видео, кружки, голосовые, музыку и файлы в исходном качестве"
                    : "Сохраняет только переписку в чат-транскрипт и JSON-дамп без загрузки медиа"
            );
            table.AddRow(
                "[grey]4[/]",
                "Пропуск уже скачанных файлов",
                _config.SkipExisting ? "[bold green]√ Чисто (Докачка вкл)[/]" : "[bold yellow]Перезапись всех файлов[/]",
                "Если файл уже присутствует на диске с правильным размером, он пропускается"
            );
            table.AddRow(
                "[grey]5[/]",
                "Локальный сетевой туннель",
                !string.IsNullOrWhiteSpace(_config.ProxyUrl) ? "[bold green]√ MTProxy (127.0.0.1:1443)[/]" : "[grey]Прямой TCP[/]",
                "Туннелирование MTProto-трафика через локальный TgWsProxy для обхода блокировок"
            );
            table.AddRow(
                "[grey]6[/]",
                "Папка для сохранения дампа",
                "[white]dumps/...[/]",
                $"Файлы складываются в папку: {Path.GetFullPath(_config.OutputFolder)}"
            );

            AnsiConsole.Write(table);

            Console.WriteLine();
            var menuPrompt = new SelectionPrompt<string>()
                .Title("[bold cyan]Выберите действие:[/] [grey](навигация ↑/↓, выбор Enter)[/]\n")
                .PageSize(10)
                .WrapAround(true)
                .HighlightStyle(new Style(foreground: Color.Cyan1, decoration: Decoration.Bold))
                .AddChoices(
                    "1. Начать выгрузку переписки и медиа (дамп выбранного чата)",
                    "2. Выбрать целевой чат из списка диалогов",
                    "3. Поиск чата по названию или @юзернейму",
                    $"4. Переключить выкачку медиа: {(_config.DownloadMedia ? "[green]ВКЛ (Все типы)[/]" : "[red]ВЫКЛ (Только текст)[/]")}",
                    "5. Сменить сессию / аккаунт",
                    "0. Выход"
                );

            string choice = AnsiConsole.Prompt(menuPrompt);

            if (choice.StartsWith("0."))
            {
                break;
            }

            if (choice.StartsWith("1."))
            {
                if (targetChat == null)
                {
                    AnsiConsole.MarkupLine("[bold red]Сначала выберите чат для выгрузки![/]");
                    Thread.Sleep(1200);
                    continue;
                }

                string safeChatTitle = SanitizePath(string.IsNullOrWhiteSpace(targetChatName) ? $"Chat_{targetChat.ID}" : targetChatName);
                string chatDumpDir = Path.Combine(_config.OutputFolder, $"{safeChatTitle}_{targetChat.ID}");
                Directory.CreateDirectory(chatDumpDir);

                var mediaDirs = new Dictionary<string, string>
                {
                    ["photo"] = Path.Combine(chatDumpDir, "media", "photos"),
                    ["video"] = Path.Combine(chatDumpDir, "media", "videos"),
                    ["document"] = Path.Combine(chatDumpDir, "media", "documents"),
                    ["voice"] = Path.Combine(chatDumpDir, "media", "voice"),
                    ["round_video"] = Path.Combine(chatDumpDir, "media", "round_video"),
                    ["audio"] = Path.Combine(chatDumpDir, "media", "audio"),
                    ["sticker"] = Path.Combine(chatDumpDir, "media", "stickers")
                };

                if (_config.DownloadMedia)
                {
                    foreach (var kvp in mediaDirs)
                    {
                        Directory.CreateDirectory(kvp.Value);
                    }
                }

                InputPeer inputPeer = targetChat.ToInputPeer();
                await RunDumpWithLiveDashboardAsync(client, inputPeer, targetChat, chatDumpDir, mediaDirs, isProtected, userDisplay);

                var postPrompt = new SelectionPrompt<string>()
                    .Title("\n[bold cyan]Действия после дампа:[/] [grey](навигация ↑/↓, выбор Enter)[/]\n")
                    .HighlightStyle(new Style(foreground: Color.Cyan1, decoration: Decoration.Bold))
                    .AddChoices(
                        "1. Открыть папку с файлами в проводнике",
                        "2. Вернуться в главное меню"
                    );

                string postChoice = AnsiConsole.Prompt(postPrompt);
                if (postChoice.StartsWith("1."))
                {
                    OpenFolderInFileManager(chatDumpDir);
                }
            }
            else if (choice.StartsWith("2."))
            {
                var picked = ShowChatSelectPrompt(allChats, "Список всех доступных диалогов и каналов");
                if (picked != null) targetChat = picked;
            }
            else if (choice.StartsWith("3."))
            {
                Console.Write("Введите строку для поиска (название или @юзернейм): ");
                string? query = Console.ReadLine()?.Trim().ToLowerInvariant();
                if (!string.IsNullOrEmpty(query))
                {
                    var matches = allChats
                        .Where(c => GetPeerTitle(c).ToLowerInvariant().Contains(query) ||
                                    (c.MainUsername?.ToLowerInvariant().Contains(query) ?? false) ||
                                    c.ID.ToString().Contains(query))
                        .ToList();

                    if (matches.Count == 0)
                    {
                        AnsiConsole.MarkupLine($"[bold yellow]Чатов по запросу '{Markup.Escape(query)}' не найдено.[/]");
                        Thread.Sleep(1500);
                    }
                    else
                    {
                        var picked = ShowChatSelectPrompt(matches, $"Результаты поиска по '{query}'");
                        if (picked != null) targetChat = picked;
                    }
                }
            }
            else if (choice.StartsWith("4."))
            {
                _config.DownloadMedia = !_config.DownloadMedia;
                SaveConfig(_config);
            }
            else if (choice.StartsWith("5."))
            {
                sessionPath = SelectOrCreateSession(_config);
                AnsiConsole.MarkupLine("[green]Сессия изменена. Перезапустите приложение для авторизации под новым аккаунтом.[/]");
                Thread.Sleep(2000);
                break;
            }
        }

        AnsiConsole.MarkupLine("\n[grey]Завершение работы программы. До свидания![/]");
    }

    private sealed class ChatSelectionChoice
    {
        public IPeerInfo? Peer { get; set; }
        public bool IsBack { get; set; }
        public string SearchText { get; set; } = "";
        public string FormattedLabel { get; set; } = "";

        public override string ToString() => IsBack ? "Назад Назад Отмена Back" : SearchText;
    }

    private static IPeerInfo? ShowChatSelectPrompt(List<IPeerInfo> chats, string subtitle)
    {
        PrintHeader("Выбор целевого чата для дампа");
        AnsiConsole.Write(new Markup($"[bold white]{Markup.Escape(subtitle)} (Всего: {chats.Count})[/]\n").Centered());

        var prompt = new SelectionPrompt<ChatSelectionChoice>()
            .Title("[bold cyan]Целевой чат / канал:[/] [grey](навигация ↑/↓, быстрый поиск вводом текста, выбор Enter)[/]\n")
            .PageSize(15)
            .WrapAround(true)
            .HighlightStyle(new Style(foreground: Color.Cyan1, decoration: Decoration.Bold))
            .UseConverter(c => c.FormattedLabel);

        try
        {
            prompt.EnableSearch();
            prompt.SearchPlaceholderText = "[grey](Начните ввод текста для поиска...)[/]";
        }
        catch
        {
        }

        prompt.AddChoice(new ChatSelectionChoice
        {
            IsBack = true,
            SearchText = "Назад",
            FormattedLabel = "[bold grey]← Назад в главное меню (отмена)[/]"
        });

        for (int i = 0; i < chats.Count; i++)
        {
            var p = chats[i];
            string type = p switch
            {
                Channel c when c.IsChannel => "[cyan][[Канал]][/]",
                Channel => "[magenta][[Супергруппа]][/]",
                Chat => "[green][[Группа]][/]",
                User => "[yellow][[Диалог]][/]",
                _ => "[grey][[Чат]][/]"
            };

            bool noSave = IsChatNoForwards(p);
            string prot = noSave ? " [bold red]! Защищен[/]" : " [green]√ Обычный[/]";
            string title = Markup.Escape(Truncate(GetPeerTitle(p), 32));
            string details = $"ID: {p.ID}" + (!string.IsNullOrEmpty(p.MainUsername) ? $" (@{Markup.Escape(p.MainUsername)})" : "");

            prompt.AddChoice(new ChatSelectionChoice
            {
                Peer = p,
                SearchText = $"{GetPeerTitle(p)} {p.MainUsername} {p.ID}",
                FormattedLabel = $"{i + 1,3}. {type}{prot} [white]{title}[/] [grey]({details})[/]"
            });
        }

        var selected = AnsiConsole.Prompt(prompt);
        if (selected.IsBack) return null;
        return selected.Peer;
    }

    private static Func<string, string?> ConfigCallback(string sessionPath)
    {
        return what => what switch
        {
            "api_id" => _config.ApiId.ToString(),
            "api_hash" => _config.ApiHash,
            "session_pathname" => sessionPath,
            "phone_number" => PromptPhone(),
            "verification_code" => PromptCode(),
            "password" => PromptPassword(),
            _ => null
        };
    }

    private static string PromptPhone()
    {
        Console.Write("Введите номер телефона с кодом страны (например +79001234567): ");
        return Console.ReadLine()?.Trim() ?? "";
    }

    private static string PromptCode()
    {
        Console.Write("Введите код подтверждения Telegram: ");
        return Console.ReadLine()?.Trim() ?? "";
    }

    private static string PromptPassword()
    {
        Console.Write("Введите пароль двухфакторной аутентификации (2FA): ");
        return ReadPassword();
    }

    private static async Task RunDumpWithLiveDashboardAsync(
        Client client,
        InputPeer peer,
        IPeerInfo peerInfo,
        string dumpDir,
        Dictionary<string, string> mediaDirs,
        bool isChatProtected,
        string userDisplay)
    {
        string chatTitle = GetPeerTitle(peerInfo);
        string transcriptFile = Path.Combine(dumpDir, "chat_history.txt");
        string jsonRecordsFile = Path.Combine(dumpDir, "chat_history.json");
        string summaryFile = Path.Combine(dumpDir, "dump_summary.txt");

        string proxyInfo = !string.IsNullOrWhiteSpace(_config.ProxyUrl) ? "127.0.0.1:1443 (TgWsProxy)" : "Прямой TCP";

        var stats = new LiveStats
        {
            ChatTitle = chatTitle,
            ChatId = peerInfo.ID,
            ProtectionStatus = isChatProtected ? "[bold red]! Защищен (Обход MTProto)[/]" : "[green]√ Чисто (Обычный чат)[/]",
            AccountName = userDisplay,
            OutputDir = dumpDir
        };

        int offsetId = 0;
        int totalMessagesDumped = 0;
        int totalMediaDownloaded = 0;
        long totalBytesDownloaded = 0;
        var allRecords = new List<DumpedMessageRecord>();

        using var textWriter = new StreamWriter(transcriptFile, append: false, Encoding.UTF8);
        await textWriter.WriteLineAsync("================================================================================");
        await textWriter.WriteLineAsync($"CHAT DUMP: {chatTitle} (ID: {peerInfo.ID})");
        await textWriter.WriteLineAsync($"ACCOUNT: {userDisplay}");
        await textWriter.WriteLineAsync($"START TIMESTAMP UTC: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        await textWriter.WriteLineAsync($"PROTECTION BYPASS: Active MTProto Raw Binary Stream");
        await textWriter.WriteLineAsync("================================================================================\n");

        var startTime = DateTime.UtcNow;

        await AnsiConsole.Live(stats.BuildRenderable(proxyInfo))
            .AutoClear(false)
            .StartAsync(async ctx =>
            {
                stats.CurrentAction = "Начало сбора истории сообщений...";
                ctx.UpdateTarget(stats.BuildRenderable(proxyInfo));
                ctx.Refresh();

                while (!Cts.IsCancellationRequested)
                {
                    stats.CurrentAction = $"Запрос блока сообщений от offset #{offsetId}...";
                    ctx.UpdateTarget(stats.BuildRenderable(proxyInfo));
                    ctx.Refresh();

                    Messages_MessagesBase history;
                    try
                    {
                        history = await ExecuteWithFloodRetry(
                            async () => await client.Messages_GetHistory(peer, offset_id: offsetId, limit: 100),
                            $"GetHistory({offsetId})");
                    }
                    catch (Exception ex)
                    {
                        stats.CurrentAction = $"Ошибка чтения блока: {ex.Message}";
                        ctx.UpdateTarget(stats.BuildRenderable(proxyInfo));
                        ctx.Refresh();
                        break;
                    }

                    if (history.Messages == null || history.Messages.Length == 0)
                    {
                        stats.CurrentAction = "Достигнуто начало истории чата.";
                        break;
                    }

                    int batchLowestId = int.MaxValue;

                    foreach (var rawMsg in history.Messages)
                    {
                        if (Cts.IsCancellationRequested) break;

                        if (rawMsg is not Message msg)
                        {
                            if (rawMsg != null && rawMsg.ID < batchLowestId)
                            {
                                batchLowestId = rawMsg.ID;
                            }
                            continue;
                        }

                        if (msg.id < batchLowestId)
                        {
                            batchLowestId = msg.id;
                        }

                        totalMessagesDumped++;
                        stats.MessagesCrawled = totalMessagesDumped;

                        string senderName = ResolveSenderName(history, msg);
                        bool msgProtected = msg.flags.HasFlag(Message.Flags.noforwards);

                        var record = new DumpedMessageRecord
                        {
                            Id = msg.id,
                            DateUtc = msg.date,
                            SenderId = msg.from_id != null ? msg.from_id.ID : msg.peer_id.ID,
                            SenderName = senderName,
                            ReplyToMsgId = msg.reply_to is MessageReplyHeader r ? r.reply_to_msg_id : null,
                            Text = msg.message ?? "",
                            IsProtectedOriginal = msgProtected
                        };

                        string mediaLogText = "";

                        if (_config.DownloadMedia && msg.media != null)
                        {
                            long lastTick = Environment.TickCount64;
                            long lastBytes = 0;

                            var result = await DownloadMessageMediaAsync(
                                client, msg, mediaDirs, (current, total) =>
                                {
                                    stats.CurrentFileBytes = current;
                                    stats.CurrentFileTotal = total;

                                    long now = Environment.TickCount64;
                                    if (now - lastTick >= 150)
                                    {
                                        long deltaBytes = current - lastBytes;
                                        double deltaSec = (now - lastTick) / 1000.0;
                                        stats.CurrentSpeedBytesPerSec = deltaSec > 0 ? deltaBytes / deltaSec : 0;
                                        lastBytes = current;
                                        lastTick = now;

                                        ctx.UpdateTarget(stats.BuildRenderable(proxyInfo));
                                        ctx.Refresh();
                                    }
                                },
                                (action, filename) =>
                                {
                                    stats.CurrentAction = $"{action}: {filename}";
                                    stats.CurrentFileName = filename;
                                    ctx.UpdateTarget(stats.BuildRenderable(proxyInfo));
                                    ctx.Refresh();
                                });

                            if (!string.IsNullOrEmpty(result.filename))
                            {
                                record.MediaCategory = result.category;
                                record.MediaFileName = result.filename;
                                record.MediaSizeBytes = result.bytes;
                                totalMediaDownloaded++;
                                totalBytesDownloaded += result.bytes;

                                stats.TotalBytesDownloaded = totalBytesDownloaded;
                                IncrementCategory(stats, result.category);
                                mediaLogText = $" [Attachment: {result.category}/{result.filename} ({FormatBytes(result.bytes)})]";
                            }

                            stats.CurrentFileBytes = 0;
                            stats.CurrentFileTotal = 0;
                            stats.CurrentSpeedBytesPerSec = 0;
                        }

                        allRecords.Add(record);

                        string replyPart = record.ReplyToMsgId.HasValue ? $" (in reply to #{record.ReplyToMsgId})" : "";
                        string protTag = record.IsProtectedOriginal ? " [PROTECTED_MSG]" : "";
                        await textWriter.WriteLineAsync($"[{msg.date:yyyy-MM-dd HH:mm:ss} UTC] [ID: {msg.id}]{protTag} {senderName}{replyPart}:");
                        if (!string.IsNullOrWhiteSpace(msg.message))
                        {
                            await textWriter.WriteLineAsync(msg.message);
                        }
                        if (!string.IsNullOrEmpty(mediaLogText))
                        {
                            await textWriter.WriteLineAsync(mediaLogText);
                        }
                        await textWriter.WriteLineAsync(new string('-', 80));

                        if (totalMessagesDumped % 15 == 0)
                        {
                            ctx.UpdateTarget(stats.BuildRenderable(proxyInfo));
                            ctx.Refresh();
                        }
                    }

                    if (batchLowestId >= offsetId && offsetId != 0)
                    {
                        break;
                    }

                    offsetId = batchLowestId;

                    if (history.Messages.Length < 100)
                    {
                        break;
                    }

                    await Task.Delay(80, Cts.Token);
                }

                stats.CurrentAction = "Сохранение структурированных дампов (JSON / TXT)...";
                ctx.UpdateTarget(stats.BuildRenderable(proxyInfo));
                ctx.Refresh();
            });

        var duration = DateTime.UtcNow - startTime;

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        string jsonContent = JsonSerializer.Serialize(allRecords, jsonOptions);
        await File.WriteAllTextAsync(jsonRecordsFile, jsonContent, Encoding.UTF8);

        var summaryBuilder = new StringBuilder();
        summaryBuilder.AppendLine("================================================================================");
        summaryBuilder.AppendLine($"DUMP SUMMARY FOR CHAT: {chatTitle} (ID: {peerInfo.ID})");
        summaryBuilder.AppendLine($"ACCOUNT: {userDisplay}");
        summaryBuilder.AppendLine($"TOTAL MESSAGES EXTRACTED: {totalMessagesDumped}");
        summaryBuilder.AppendLine($"TOTAL MEDIA ATTACHMENTS SAVED: {totalMediaDownloaded}");
        summaryBuilder.AppendLine($"TOTAL BYTES DOWNLOADED: {FormatBytes(totalBytesDownloaded)}");
        summaryBuilder.AppendLine($"EXECUTION TIME: {duration:hh\\:mm\\:ss}");
        summaryBuilder.AppendLine($"OUTPUT PATH: {dumpDir}");
        summaryBuilder.AppendLine("================================================================================");

        string summaryText = summaryBuilder.ToString();
        await File.WriteAllTextAsync(summaryFile, summaryText, Encoding.UTF8);

        Console.WriteLine();
        var reportTable = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey37).Expand();
        reportTable.Title("[bold green]√ ВЫГРУЗКА УСПЕШНО ЗАВЕРШЕНА[/]");
        reportTable.AddColumn(new TableColumn("[bold grey]#[/]").Centered().Width(4));
        reportTable.AddColumn(new TableColumn("[bold white]Компонент[/]").Width(34));
        reportTable.AddColumn(new TableColumn("[bold white]Итог / Значение[/]").Width(26));
        reportTable.AddColumn(new TableColumn("[bold white]Описание / Результат[/]"));

        reportTable.AddRow("[grey]1[/]", "Сохраненные сообщения", $"[bold green]√ {totalMessagesDumped:N0}[/]", "chat_history.txt и chat_history.json");
        reportTable.AddRow("[grey]2[/]", "Фотографии", $"[cyan]√ {stats.PhotosSaved}[/]", "media/photos/ в максимальном качестве");
        reportTable.AddRow("[grey]3[/]", "Видео и круглые видео", $"[magenta]√ {stats.VideosSaved + stats.RoundVideoSaved}[/]", "media/videos/ и media/round_video/");
        reportTable.AddRow("[grey]4[/]", "Голосовые и музыка", $"[yellow]√ {stats.AudioSaved + stats.VoiceSaved}[/]", "media/voice/ (.ogg) и media/audio/ (.mp3)");
        reportTable.AddRow("[grey]5[/]", "Документы и файлы", $"[blue]√ {stats.DocsSaved}[/]", "media/documents/ без сжатия");
        reportTable.AddRow("[grey]6[/]", "Общий объем загрузки", $"[bold gold1]√ {FormatBytes(totalBytesDownloaded)}[/]", "Итоговый вес выгруженного на диск архива");
        reportTable.AddRow("[grey]7[/]", "Время выполнения", $"[white]{duration:hh\\:mm\\:ss}[/]", "Затрачено времени на полную выгрузку");
        reportTable.AddRow("[grey]8[/]", "Путь к папке на диске", "[underline cyan]Открыть папку[/]", dumpDir);

        AnsiConsole.Write(reportTable);
    }

    private static void IncrementCategory(LiveStats d, string? category)
    {
        switch (category)
        {
            case "photo": d.PhotosSaved++; break;
            case "video": d.VideosSaved++; break;
            case "round_video": d.RoundVideoSaved++; break;
            case "audio": d.AudioSaved++; break;
            case "voice": d.VoiceSaved++; break;
            case "sticker": d.StickersSaved++; break;
            default: d.DocsSaved++; break;
        }
    }

    private static async Task<(string? category, string? filename, long bytes)> DownloadMessageMediaAsync(
        Client client,
        Message msg,
        Dictionary<string, string> mediaDirs,
        Action<long, long> progress,
        Action<string, string> statusUpdate)
    {
        try
        {
            if (msg.media is MessageMediaPhoto mmp && mmp.photo is Photo photo)
            {
                string targetDir = mediaDirs["photo"];
                string filename = $"{msg.id}_photo_{photo.id}.jpg";
                string fullPath = Path.Combine(targetDir, filename);

                if (_config.SkipExisting && File.Exists(fullPath) && new FileInfo(fullPath).Length > 0)
                {
                    return ("photo", filename, new FileInfo(fullPath).Length);
                }

                statusUpdate("Скачивание фото", filename);

                await using var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await ExecuteWithFloodRetry(async () =>
                {
                    fs.Position = 0;
                    fs.SetLength(0);
                    await client.DownloadFileAsync(photo, fs, progress: (c, t) => progress(c, t));
                }, $"DownloadPhoto_{msg.id}");

                long length = fs.Length;
                return ("photo", filename, length);
            }

            if (msg.media is MessageMediaDocument mmd && mmd.document is Document doc)
            {
                if (_config.MaxFileSizeMb > 0 && doc.size > (long)_config.MaxFileSizeMb * 1024 * 1024)
                {
                    return (null, null, 0);
                }

                string category = DetermineDocumentCategory(doc);
                string targetDir = mediaDirs.TryGetValue(category, out var dir) ? dir : mediaDirs["document"];

                string safeName = DetermineDocumentFileName(doc, msg.id, category);
                string fullPath = Path.Combine(targetDir, safeName);

                if (_config.SkipExisting && File.Exists(fullPath) && new FileInfo(fullPath).Length == doc.size)
                {
                    return (category, safeName, doc.size);
                }

                statusUpdate($"Скачивание {category}", safeName);

                await using var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await ExecuteWithFloodRetry(async () =>
                {
                    fs.Position = 0;
                    fs.SetLength(0);
                    await client.DownloadFileAsync(doc, fs, progress: (c, t) => progress(c, t));
                }, $"DownloadDoc_{msg.id}");

                long length = fs.Length;
                return (category, safeName, length);
            }
        }
        catch
        {
        }

        return (null, null, 0);
    }

    private static string DetermineDocumentCategory(Document doc)
    {
        if (doc.attributes != null)
        {
            if (doc.attributes.OfType<DocumentAttributeVideo>().Any(v => v.flags.HasFlag(DocumentAttributeVideo.Flags.round_message)))
                return "round_video";

            if (doc.attributes.OfType<DocumentAttributeVideo>().Any())
                return "video";

            if (doc.attributes.OfType<DocumentAttributeAudio>().Any(a => a.flags.HasFlag(DocumentAttributeAudio.Flags.voice)))
                return "voice";

            if (doc.attributes.OfType<DocumentAttributeAudio>().Any())
                return "audio";

            if (doc.attributes.OfType<DocumentAttributeSticker>().Any())
                return "sticker";
        }

        string mime = doc.mime_type?.ToLowerInvariant() ?? "";
        if (mime.StartsWith("video/")) return "video";
        if (mime.StartsWith("audio/")) return "audio";
        if (mime.StartsWith("image/")) return "photo";

        return "document";
    }

    private static string DetermineDocumentFileName(Document doc, int msgId, string category)
    {
        string baseName = doc.Filename;

        if (string.IsNullOrWhiteSpace(baseName))
        {
            string ext = GetExtensionFromMime(doc.mime_type, category);
            baseName = $"{msgId}_{category}_{doc.id}{ext}";
        }
        else
        {
            baseName = $"{msgId}_{SanitizePath(baseName)}";
        }

        return baseName;
    }

    private static string GetExtensionFromMime(string? mime, string category)
    {
        if (string.IsNullOrWhiteSpace(mime))
        {
            return category switch
            {
                "round_video" or "video" => ".mp4",
                "voice" => ".ogg",
                "audio" => ".mp3",
                "sticker" => ".webp",
                _ => ".bin"
            };
        }

        return mime.ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "video/mp4" => ".mp4",
            "video/webm" => ".webm",
            "video/quicktime" => ".mov",
            "video/x-matroska" => ".mkv",
            "audio/ogg" => ".ogg",
            "audio/mpeg" => ".mp3",
            "audio/mp4" => ".m4a",
            "audio/wav" or "audio/x-wav" => ".wav",
            "application/pdf" => ".pdf",
            "application/x-tgsticker" => ".tgs",
            "application/zip" => ".zip",
            _ => ".bin"
        };
    }

    private static string ResolveSenderName(Messages_MessagesBase history, Message msg)
    {
        if (msg.from_id != null)
        {
            var senderPeer = history.UserOrChat(msg.from_id);
            if (senderPeer != null)
            {
                return FormatPeerName(senderPeer);
            }
        }

        if (msg.peer_id != null)
        {
            var chatPeer = history.UserOrChat(msg.peer_id);
            if (chatPeer != null)
            {
                return FormatPeerName(chatPeer);
            }
        }

        return "Неизвестный отправитель";
    }

    private static string GetPeerTitle(IPeerInfo peer)
    {
        if (peer is User user)
        {
            string name = $"{user.first_name} {user.last_name}".Trim();
            if (string.IsNullOrWhiteSpace(name)) name = user.username ?? $"User_{user.id}";
            return name;
        }

        if (peer is Channel channel)
        {
            return channel.title ?? $"Channel_{channel.id}";
        }

        if (peer is Chat chat)
        {
            return chat.title ?? $"Chat_{chat.id}";
        }

        return peer.ToString() ?? $"Peer_{peer.ID}";
    }

    private static string FormatPeerName(IPeerInfo peer)
    {
        string title = GetPeerTitle(peer);
        if (!string.IsNullOrEmpty(peer.MainUsername))
        {
            return $"{title} (@{peer.MainUsername})";
        }
        return title;
    }

    private static bool IsChatNoForwards(IPeerInfo peer)
    {
        if (peer is Channel ch)
        {
            return ch.flags.HasFlag(Channel.Flags.noforwards);
        }
        if (peer is Chat chat)
        {
            return chat.flags.HasFlag(Chat.Flags.noforwards);
        }
        return false;
    }

    private static string SelectOrCreateSession(DumperConfig config)
    {
        var sessionFiles = Directory.GetFiles(config.SessionsFolder, "*.session")
            .Concat(Directory.GetFiles(".", "*.session"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (sessionFiles.Count > 0)
        {
            PrintHeader("Выбор активной сессии Telegram");
            AnsiConsole.Write(new Markup("[bold white]Текущее состояние локальных аккаунтов на компьютере[/]\n").Centered());

            var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey37).Expand();
            table.AddColumn(new TableColumn("[bold grey]#[/]").Centered().Width(4));
            table.AddColumn(new TableColumn("[bold white]Файл сессии[/]").Width(34));
            table.AddColumn(new TableColumn("[bold white]Статус[/]").Width(26));
            table.AddColumn(new TableColumn("[bold white]Путь на диске[/]"));

            for (int i = 0; i < sessionFiles.Count; i++)
            {
                string fileName = Path.GetFileName(sessionFiles[i]);
                table.AddRow($"[grey]{i + 1}[/]", $"[green]{fileName}[/]", "[green]√ Готов к авторизации[/]", sessionFiles[i]);
            }

            AnsiConsole.Write(table);

            Console.WriteLine();
            var sessionPrompt = new SelectionPrompt<string>()
                .Title("[bold cyan]Выберите действие:[/] [grey](навигация ↑/↓, выбор Enter)[/]\n")
                .PageSize(10)
                .WrapAround(true)
                .HighlightStyle(new Style(foreground: Color.Cyan1, decoration: Decoration.Bold));

            for (int i = 0; i < sessionFiles.Count; i++)
            {
                string fileName = Path.GetFileName(sessionFiles[i]);
                sessionPrompt.AddChoice($"{i + 1}. Использовать аккаунт '{fileName}'");
            }
            sessionPrompt.AddChoice($"{sessionFiles.Count + 1}. Войти под новым аккаунтом (добавить сессию)");

            string chosen = AnsiConsole.Prompt(sessionPrompt);
            for (int i = 0; i < sessionFiles.Count; i++)
            {
                if (chosen.StartsWith($"{i + 1}."))
                {
                    return sessionFiles[i];
                }
            }
        }

        Console.Write("Введите имя для новой сессии (например 'main' или 'work'): ");
        string? sessionName = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(sessionName))
        {
            sessionName = $"session_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
        }

        if (!sessionName.EndsWith(".session", StringComparison.OrdinalIgnoreCase))
        {
            sessionName += ".session";
        }

        string newPath = Path.Combine(config.SessionsFolder, sessionName);
        return newPath;
    }

    private static async Task<T> ExecuteWithFloodRetry<T>(Func<Task<T>> action, string operationName)
    {
        while (true)
        {
            try
            {
                return await action();
            }
            catch (RpcException rpcEx) when (rpcEx.Code == 420)
            {
                int waitSeconds = ExtractFloodWaitSeconds(rpcEx.Message);
                AnsiConsole.MarkupLine($"\n[bold yellow][[FLOOD_WAIT]] Лимит запросов Telegram для '{operationName}'. Ожидание {waitSeconds} сек...[/]");
                await Task.Delay(waitSeconds * 1000 + 1000, Cts.Token);
            }
        }
    }

    private static async Task ExecuteWithFloodRetry(Func<Task> action, string operationName)
    {
        while (true)
        {
            try
            {
                await action();
                return;
            }
            catch (RpcException rpcEx) when (rpcEx.Code == 420)
            {
                int waitSeconds = ExtractFloodWaitSeconds(rpcEx.Message);
                AnsiConsole.MarkupLine($"\n[bold yellow][[FLOOD_WAIT]] Лимит запросов Telegram для '{operationName}'. Ожидание {waitSeconds} сек...[/]");
                await Task.Delay(waitSeconds * 1000 + 1000, Cts.Token);
            }
        }
    }

    private static int ExtractFloodWaitSeconds(string message)
    {
        var match = Regex.Match(message, @"FLOOD_WAIT_(\d+)");
        if (match.Success && int.TryParse(match.Groups[1].Value, out int seconds))
        {
            return seconds;
        }
        return 5;
    }

    private static DumperConfig LoadOrCreateConfig()
    {
        if (File.Exists(ConfigFileName))
        {
            try
            {
                string json = File.ReadAllText(ConfigFileName, Encoding.UTF8);
                var loaded = JsonSerializer.Deserialize<DumperConfig>(json);
                if (loaded != null) return loaded;
            }
            catch
            {
            }
        }

        var cfg = new DumperConfig();
        SaveConfig(cfg);
        return cfg;
    }

    private static void SaveConfig(DumperConfig cfg)
    {
        try
        {
            string json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigFileName, json, Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static void EnsureApiCredentials(DumperConfig cfg)
    {
        string? envId = Environment.GetEnvironmentVariable("TG_API_ID");
        string? envHash = Environment.GetEnvironmentVariable("TG_API_HASH");

        if (cfg.ApiId == 0 && !string.IsNullOrEmpty(envId) && int.TryParse(envId, out int parsedId))
        {
            cfg.ApiId = parsedId;
        }

        if (string.IsNullOrEmpty(cfg.ApiHash) && !string.IsNullOrEmpty(envHash))
        {
            cfg.ApiHash = envHash;
        }

        if (cfg.ApiId == 0 || string.IsNullOrWhiteSpace(cfg.ApiHash))
        {
            cfg.ApiId = DefaultApiId;
            cfg.ApiHash = DefaultApiHash;
            SaveConfig(cfg);
        }
    }

    private static void EnsureProxyConfig(DumperConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.ProxyUrl))
        {
            return;
        }

        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string tgWsConfigPath = Path.Combine(appData, "TgWsProxy", "config.json");
            if (File.Exists(tgWsConfigPath))
            {
                string json = File.ReadAllText(tgWsConfigPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string host = root.TryGetProperty("host", out var h) ? h.GetString() ?? "127.0.0.1" : "127.0.0.1";
                int port = root.TryGetProperty("port", out var p) ? p.GetInt32() : 1443;
                string? secret = root.TryGetProperty("secret", out var s) ? s.GetString() : null;

                if (!string.IsNullOrEmpty(secret))
                {
                    cfg.ProxyUrl = $"tg://proxy?server={host}&port={port}&secret={secret}";
                    SaveConfig(cfg);
                }
            }
        }
        catch
        {
        }
    }

    private static string SanitizePath(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (char c in name)
        {
            sb.Append(invalid.Contains(c) ? '_' : c);
        }
        return sb.ToString().Trim();
    }

    private static string Truncate(string str, int maxLen)
    {
        if (string.IsNullOrEmpty(str)) return "";
        return str.Length <= maxLen ? str : str.Substring(0, maxLen - 3) + "...";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < units.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {units[order]}";
    }

    private static string ReadPassword()
    {
        var pwd = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace && pwd.Length > 0)
            {
                pwd.Remove(pwd.Length - 1, 1);
                Console.Write("\b \b");
            }
            else if (!char.IsControl(key.KeyChar))
            {
                pwd.Append(key.KeyChar);
                Console.Write("*");
            }
        }
        Console.WriteLine();
        return pwd.ToString();
    }

    private static void PrintHeader(string title, string? userStr = null)
    {
        try { AnsiConsole.Clear(); } catch { }

        var rule = new Rule($"[bold deepskyblue1]{Markup.Escape(title)}[/]")
        {
            Justification = Justify.Center
        };
        rule.RuleStyle("deepskyblue1");
        AnsiConsole.Write(rule);

        string dev = "Telegram MTProto Client";
        string proxyStr = !string.IsNullOrWhiteSpace(_config.ProxyUrl) ? "127.0.0.1:1443 (TgWsProxy Active)" : "Прямое подключение";
        AnsiConsole.MarkupLine($"[grey]Разработчик:[/] [white]{dev}[/] • [grey]Туннель:[/] [cyan]{Markup.Escape(proxyStr)}[/]");
        AnsiConsole.MarkupLine($"[grey]Режим прав:[/]  [bold green]Авторизован (MTProto 2.0 · Direct Bypass)[/]");
        if (!string.IsNullOrEmpty(userStr))
        {
            AnsiConsole.MarkupLine($"[grey]Аккаунт:[/]     [white]{Markup.Escape(userStr)}[/]");
        }
        Console.WriteLine();
    }

    private static void OpenFolderInFileManager(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer.exe", fullPath) { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", fullPath);
            }
            else if (OperatingSystem.IsLinux())
            {
                Process.Start("xdg-open", fullPath);
            }
        }
        catch
        {
        }
    }
}
