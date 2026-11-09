using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Campus.MealLedger
{
    public class Program
    {
        private static readonly string[] MealTypes = { "Завтрак", "Обед", "Полдник", "Ужин", "Буфет" };

        private static readonly Dictionary<string, TimeSpan> ServingStart = new()
        {
            ["Завтрак"] = new TimeSpan(8, 0, 0),
            ["Обед"] = new TimeSpan(12, 30, 0),
            ["Полдник"] = new TimeSpan(15, 30, 0),
            ["Ужин"] = new TimeSpan(18, 0, 0),
            ["Буфет"] = new TimeSpan(10, 0, 0)
        };

        public static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            var opts = LoadOptions(args);
            var date = opts.ReportDate;
            Console.WriteLine($"Campus.MealLedger 1.0.0, дата ведомости {date:dd.MM.yyyy}");
            Console.WriteLine($"Источник: {(opts.Offline ? "локальная генерация (--offline)" : opts.SourceUrl)}");

            List<SourcePost> posts;
            if (opts.Offline)
            {
                posts = Enumerable.Range(1, 100)
                    .Select(i => new SourcePost(i % 10 + 1, i, $"Заголовок записи номер {i}", "body"))
                    .ToList();
            }
            else
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Campus.MealLedger/1.0");
                try
                {
                    posts = await http.GetFromJsonAsync<List<SourcePost>>(opts.SourceUrl) ?? new();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Источник недоступен: {ex.Message}");
                    Console.Error.WriteLine("Подсказка: запустите с аргументом --offline, если сеть недоступна.");
                    return 2;
                }
            }

            if (posts.Count == 0 || posts.Select(p => p.Id).Distinct().Count() != posts.Count)
            {
                Console.Error.WriteLine($"Входной контроль не пройден: записей {posts.Count}, дубликаты ID есть.");
                return 3;
            }
            Console.WriteLine($"Получено записей: {posts.Count}");

            var transactions = Simulate(posts, opts, date);
            var ledger = transactions
                .GroupBy(t => t.MealType)
                .OrderBy(g => Array.IndexOf(MealTypes, g.Key))
                .Select(g => new LedgerRow(
                    g.Key,
                    g.Count(),
                    g.Select(t => t.CardId).Distinct().Count(),
                    g.Sum(t => t.Amount),
                    g.Sum(t => t.Subsidy),
                    g.Sum(t => t.ToPay),
                    Math.Round(g.Average(t => t.Amount), 2)))
                .ToList();

            var detailTotal = transactions.Sum(t => t.Amount);
            var ledgerTotal = ledger.Sum(r => r.Amount);
            if (detailTotal != ledgerTotal || ledger.Sum(r => r.Count) != transactions.Count)
            {
                Console.Error.WriteLine($"Расхождение итогов: журнал {detailTotal:0.00}, ведомость {ledgerTotal:0.00}");
                return 3;
            }

            PrintLedger(ledger);

            Directory.CreateDirectory(opts.OutputDirectory);
            var detailFile = WriteDetail(opts.OutputDirectory, date, transactions);
            var ledgerFile = WriteLedgerFile(opts.OutputDirectory, date, opts.CanteenName, ledger);
            var hashFile = WriteHashes(opts.OutputDirectory, date, detailFile, ledgerFile);

            Console.WriteLine();
            Console.WriteLine($"Журнал транзакций: {detailFile}");
            Console.WriteLine($"Ведомость: {ledgerFile}");
            Console.WriteLine($"Контрольные суммы: {hashFile}");
            Console.WriteLine($"Итого: {transactions.Count} транзакций на {detailTotal:0.00} руб., дотация {ledger.Sum(r => r.Subsidy):0.00} руб.");
            return 0;
        }

        private static List<MealTransaction> Simulate(List<SourcePost> posts, Options opts, DateTime date)
        {
            var result = new List<MealTransaction>();
            foreach (var post in posts.OrderBy(p => p.Id))
            {
                var mealType = MealTypes[(post.Id - 1) % MealTypes.Length];
                var payer = post.UserId <= 3 ? "Льготник" : post.UserId <= 8 ? "Студент" : "Сотрудник";
                var extra = post.Title.Length % 7 * 10m;
                var amount = opts.Prices[mealType] + extra;
                var subsidy = payer == "Льготник" && opts.SubsidizedMeals.Contains(mealType) ? opts.Prices[mealType] : 0m;
                var time = date + ServingStart[mealType] + TimeSpan.FromMinutes(post.Id * 7 % 90);
                result.Add(new MealTransaction(
                    $"MEAL-{date:yyyyMMdd}-{post.Id:D4}",
                    time,
                    $"POS-{post.Id % 3 + 1:D2}",
                    $"CARD-{post.UserId:D4}",
                    payer,
                    mealType,
                    amount,
                    subsidy,
                    $"posts/{post.Id}"));
            }
            return result;
        }

        private static void PrintLedger(List<LedgerRow> ledger)
        {
            Console.WriteLine();
            Console.WriteLine($"{"Категория",-10} {"Транз.",6} {"Посет.",6} {"Сумма",10} {"Дотация",10} {"К оплате",10} {"Ср. чек",10}");
            foreach (var r in ledger)
                Console.WriteLine($"{r.MealType,-10} {r.Count,6} {r.Visitors,6} {r.Amount,10:0.00} {r.Subsidy,10:0.00} {r.ToPay,10:0.00} {r.AverageCheck,10:0.00}");
            Console.WriteLine($"{"ИТОГО",-10} {ledger.Sum(r => r.Count),6} {"",6} {ledger.Sum(r => r.Amount),10:0.00} {ledger.Sum(r => r.Subsidy),10:0.00} {ledger.Sum(r => r.ToPay),10:0.00} {"",10}");
        }

        private static string WriteDetail(string dir, DateTime date, List<MealTransaction> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(';', "ID транзакции", "Дата и время", "Терминал", "Карта", "Категория посетителя", "Категория питания", "Сумма, руб.", "Дотация, руб.", "К оплате, руб.", "Источник"));
            foreach (var t in rows)
                sb.AppendLine(string.Join(';', t.TransactionId, t.Timestamp.ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("ru-RU")), t.Terminal, t.CardId, t.PayerCategory, t.MealType, Money(t.Amount), Money(t.Subsidy), Money(t.ToPay), t.SourceRef));
            var path = Path.Combine(dir, $"meal_transactions_{date:yyyy-MM-dd}.csv");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            return path;
        }

        private static string WriteLedgerFile(string dir, DateTime date, string canteen, List<LedgerRow> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Ведомость учета питания;{canteen}");
            sb.AppendLine($"Дата;{date:dd.MM.yyyy}");
            sb.AppendLine($"Сформировано;{DateTime.Now:dd.MM.yyyy HH:mm:ss}");
            sb.AppendLine();
            sb.AppendLine(string.Join(';', "Категория питания", "Транзакций", "Посетителей", "Сумма, руб.", "Дотация, руб.", "К оплате, руб.", "Средний чек, руб."));
            foreach (var r in rows)
                sb.AppendLine(string.Join(';', r.MealType, r.Count, r.Visitors, Money(r.Amount), Money(r.Subsidy), Money(r.ToPay), Money(r.AverageCheck)));
            sb.AppendLine(string.Join(';', "ИТОГО", rows.Sum(r => r.Count), "", Money(rows.Sum(r => r.Amount)), Money(rows.Sum(r => r.Subsidy)), Money(rows.Sum(r => r.ToPay)), ""));
            var path = Path.Combine(dir, $"meal_ledger_{date:yyyy-MM-dd}.csv");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            return path;
        }

        private static string WriteHashes(string dir, DateTime date, params string[] files)
        {
            var sb = new StringBuilder();
            foreach (var file in files)
            {
                using var stream = File.OpenRead(file);
                var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                sb.AppendLine($"{hash}  {Path.GetFileName(file)}");
            }
            var path = Path.Combine(dir, $"meal_ledger_{date:yyyy-MM-dd}.sha256");
            File.WriteAllText(path, sb.ToString());
            return path;
        }

        private static string Money(decimal value) => value.ToString("0.00", CultureInfo.GetCultureInfo("ru-RU"));

        private static Options LoadOptions(string[] args)
        {
            var json = File.Exists("appsettings.json") ? File.ReadAllText("appsettings.json") : "{}";
            var o = JsonSerializer.Deserialize<Options>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new Options();
            foreach (var a in args)
            {
                if (a.StartsWith("--date=") && DateTime.TryParse(a["--date=".Length..], out var d)) o.ReportDate = d;
                if (a == "--offline") o.Offline = true;
                if (a.StartsWith("--out=")) o.OutputDirectory = a["--out=".Length..];
            }
            return o;
        }
    }

    public record SourcePost(int UserId, int Id, string Title, string Body);

    public record MealTransaction(string TransactionId, DateTime Timestamp, string Terminal, string CardId, string PayerCategory, string MealType, decimal Amount, decimal Subsidy, string SourceRef)
    {
        public decimal ToPay => Amount - Subsidy;
    }

    public record LedgerRow(string MealType, int Count, int Visitors, decimal Amount, decimal Subsidy, decimal ToPay, decimal AverageCheck);

    public class Options
    {
        public string SourceUrl { get; set; } = "https://jsonplaceholder.typicode.com/posts";
        public string OutputDirectory { get; set; } = "output";
        public string CanteenName { get; set; } = "Столовая корпуса А";
        public int TimeoutSeconds { get; set; } = 15;
        public DateTime ReportDate { get; set; } = DateTime.Today;
        public bool Offline { get; set; }
        public Dictionary<string, decimal> Prices { get; set; } = new()
        {
            ["Завтрак"] = 120m,
            ["Обед"] = 250m,
            ["Полдник"] = 90m,
            ["Ужин"] = 210m,
            ["Буфет"] = 70m
        };
        public List<string> SubsidizedMeals { get; set; } = new() { "Завтрак", "Обед" };
    }
}