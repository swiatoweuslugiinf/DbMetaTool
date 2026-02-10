using FirebirdSql.Data.FirebirdClient;
using System.Xml.Linq;

namespace DbMetaTool
{
    public static class Program
    {
        // Przykładowe wywołania:
        // DbMetaTool build-db --db-dir "C:\db\fb5" --scripts-dir "C:\scripts"
        // DbMetaTool export-scripts --connection-string "..." --output-dir "C:\out"
        // DbMetaTool update-db --connection-string "..." --scripts-dir "C:\scripts"
        public static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Użycie:");
                Console.WriteLine("  build-db --db-dir <ścieżka> --scripts-dir <ścieżka>");
                Console.WriteLine("  export-scripts --connection-string <connStr> --output-dir <ścieżka>");
                Console.WriteLine("  update-db --connection-string <connStr> --scripts-dir <ścieżka>");
                return 1;
            }

            try
            {
                var command = args[0].ToLowerInvariant();

                switch (command)
                {
                    case "build-db":
                        {
                            string dbDir = GetArgValue(args, "--db-dir");
                            string scriptsDir = GetArgValue(args, "--scripts-dir");

                            BuildDatabase(dbDir, scriptsDir);
                            Console.WriteLine("Baza danych została zbudowana pomyślnie.");
                            return 0;
                        }

                    case "export-scripts":
                        {
                            string connStr = GetArgValue(args, "--connection-string");
                            string outputDir = GetArgValue(args, "--output-dir");

                            ExportScripts(connStr, outputDir);
                            Console.WriteLine("Skrypty zostały wyeksportowane pomyślnie.");
                            return 0;
                        }

                    case "update-db":
                        {
                            string connStr = GetArgValue(args, "--connection-string");
                            string scriptsDir = GetArgValue(args, "--scripts-dir");

                            UpdateDatabase(connStr, scriptsDir);
                            Console.WriteLine("Baza danych została zaktualizowana pomyślnie.");
                            return 0;
                        }

                    default:
                        Console.WriteLine($"Nieznane polecenie: {command}");
                        return 1;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Błąd: " + ex.Message);
                return -1;
            }
        }

        private static string GetArgValue(string[] args, string name)
        {
            int idx = Array.IndexOf(args, name);
            if (idx == -1 || idx + 1 >= args.Length)
                throw new ArgumentException($"Brak wymaganego parametru {name}");
            return args[idx + 1];
        }

        /// <summary>
        /// Buduje nową bazę danych Firebird 5.0 na podstawie skryptów.
        /// </summary>
        public static void BuildDatabase(string databaseDirectory, string scriptsDirectory)
        {
            if (!Directory.Exists(scriptsDirectory))
                throw new DirectoryNotFoundException($"Nie znaleziono katalogu skryptów: {scriptsDirectory}");

            Directory.CreateDirectory(databaseDirectory);

            string dbPath = Path.Combine(databaseDirectory, "database.fdb");

            if (File.Exists(dbPath))
                throw new InvalidOperationException($"Plik bazy danych już istnieje: {dbPath}");

            string connectionString =
                $"User=SYSDBA;Password=masterkey;Database={dbPath};" +
                $"DataSource=localhost;Port=3050;Dialect=3;Charset=UTF8;";

            FbConnection.CreateDatabase(connectionString, 4096, forcedWrites: true);
            Console.WriteLine($"Utworzono bazę danych: {dbPath}");

            using var connection = new FbConnection(connectionString);
            connection.Open();

            var scriptFiles = Directory
                .GetFiles(scriptsDirectory, "*.sql")
                .OrderBy(f => f)
                .ToList();

            foreach (var file in scriptFiles)
            {
                Console.WriteLine($"Wykonywanie skryptu: {Path.GetFileName(file)}");

                string scriptText = File.ReadAllText(file);

                foreach (var statement in SplitStatements(scriptText))
                {
                    if (string.IsNullOrWhiteSpace(statement))
                        continue;

                    string trimmed = statement.TrimStart();

                    using var transaction = connection.BeginTransaction();
                    try
                    {
                        string sqlToExecute = statement;

                        if (trimmed.StartsWith("CREATE PROCEDURE", StringComparison.OrdinalIgnoreCase))
                        {
                            sqlToExecute = RewriteProcedure(statement);
                        }

                        using var cmd = new FbCommand(sqlToExecute, connection, transaction);
                        cmd.ExecuteNonQuery();
                        transaction.Commit();
                    }
                    catch
                    {
                        transaction.Rollback();
                        throw;
                    }
                }
            }

            Console.WriteLine("Wszystkie skrypty zostały wykonane poprawnie.");
        }

        /// <summary>
        /// Generuje skrypty metadanych z istniejącej bazy danych Firebird 5.0.
        /// </summary>
        public static void ExportScripts(string connectionString, string outputDirectory)
        {
            Directory.CreateDirectory(outputDirectory);

            string domainsFile = Path.Combine(outputDirectory, "01_domains.sql");
            string tablesFile = Path.Combine(outputDirectory, "02_tables.sql");
            string proceduresFile = Path.Combine(outputDirectory, "03_procedures.sql");

            using var connection = new FbConnection(connectionString);
            connection.Open();

            // 1. Domeny
            using (var writer = new StreamWriter(domainsFile, false))
            {
                writer.WriteLine("-- Domeny");

                string sql = @"
                    SELECT
                        F.RDB$FIELD_NAME,
                        F.RDB$FIELD_TYPE,
                        F.RDB$FIELD_LENGTH,
                        F.RDB$FIELD_SCALE,
                        F.RDB$FIELD_PRECISION,
                        CS.RDB$CHARACTER_SET_NAME
                    FROM RDB$FIELDS F
                    LEFT JOIN RDB$CHARACTER_SETS CS
                        ON F.RDB$CHARACTER_SET_ID = CS.RDB$CHARACTER_SET_ID
                    WHERE F.RDB$SYSTEM_FLAG = 0
                    AND F.RDB$FIELD_NAME NOT STARTING WITH 'RDB$'
                    ORDER BY F.RDB$FIELD_NAME";

                using var domainCmd = new FbCommand(sql, connection);
                using var domainReader = domainCmd.ExecuteReader();

                while (domainReader.Read())
                {
                    string name = domainReader.GetString(0).Trim();
                    short fieldType = domainReader.GetInt16(1);
                    short length = domainReader.IsDBNull(2) ? (short)0 : domainReader.GetInt16(2);
                    short scale = domainReader.IsDBNull(3) ? (short)0 : domainReader.GetInt16(3);
                    short precision = domainReader.IsDBNull(4) ? (short)0 : domainReader.GetInt16(4);
                    string charset = domainReader.IsDBNull(5) ? "" : domainReader.GetString(5).Trim();

                    string typeSql;

                    switch (fieldType)
                    {
                        case 7:
                            typeSql = "SMALLINT";
                            break;
                        case 8:
                            if (scale == 0)
                            {
                                typeSql = "INTEGER";
                            }
                            else
                                typeSql = $"NUMERIC({precision},{Math.Abs(scale)})";
                            break;
                        case 16:
                            typeSql = $"NUMERIC({precision},{Math.Abs(scale)})";
                            break;
                        case 37:
                            int charLength = length;
                            if (!string.IsNullOrEmpty(charset) && charset.Equals("UTF8", StringComparison.OrdinalIgnoreCase))
                                charLength /= 4;
                            typeSql = $"VARCHAR({charLength})";
                            break;
                        case 261:
                            typeSql = "BLOB";
                            break;
                        default:
                            typeSql = "UNKNOWN";
                            break;
                    }

                    writer.WriteLine($"CREATE DOMAIN {name} {typeSql};");
                    writer.WriteLine();
                }
            }

            // 2. Tabele + kolumny
            using (var writer = new StreamWriter(tablesFile, false))
            {
                writer.WriteLine("-- Tabele");

                string tableSql = @"
                    SELECT RDB$RELATION_NAME
                    FROM RDB$RELATIONS
                    WHERE RDB$SYSTEM_FLAG = 0
                    ORDER BY RDB$RELATION_NAME";

                using var tableCmd = new FbCommand(tableSql, connection);
                using var tableReader = tableCmd.ExecuteReader();

                while (tableReader.Read())
                {
                    string tableName = tableReader.GetString(0).Trim();

                    writer.WriteLine($"CREATE TABLE {tableName} (");

                    string columnSql = @"
                        SELECT
                            RF.RDB$FIELD_NAME,
                            RF.RDB$FIELD_SOURCE
                        FROM RDB$RELATION_FIELDS RF
                        WHERE RF.RDB$RELATION_NAME = @TABLE
                        ORDER BY RF.RDB$FIELD_POSITION";

                    using var colCmd = new FbCommand(columnSql, connection);
                    colCmd.Parameters.AddWithValue("@TABLE", tableName);

                    using var colReader = colCmd.ExecuteReader();

                    var columns = new List<string>();
                    while (colReader.Read())
                    {
                        string colName = colReader.GetString(0).Trim();
                        string domain = colReader.GetString(1).Trim();
                        columns.Add($"    {colName} {domain}");
                    }

                    writer.WriteLine(string.Join(",\n", columns));
                    writer.WriteLine(");");
                    writer.WriteLine();
                }
            }

            // 3. Procedury
            using (var writer = new StreamWriter(proceduresFile, false))
            {
                writer.WriteLine("-- Procedury");

                string procSql = @"
                    SELECT
                        P.RDB$PROCEDURE_NAME,
                        P.RDB$PROCEDURE_SOURCE
                    FROM RDB$PROCEDURES P
                    WHERE RDB$SYSTEM_FLAG = 0
                    ORDER BY RDB$PROCEDURE_NAME";

                using var procCmd = new FbCommand(procSql, connection);
                using var procReader = procCmd.ExecuteReader();

                while (procReader.Read())
                {
                    string procName = procReader.GetString(0).Trim();
                    string procSource = procReader.GetString(1).TrimEnd();

                    writer.WriteLine($"-- PROCEDURE_META {procName}");

                    string paramSql = @"
                        SELECT 
                            PP.RDB$PARAMETER_NAME, 
                            PP.RDB$FIELD_SOURCE, 
                            PP.RDB$PARAMETER_TYPE
                        FROM RDB$PROCEDURE_PARAMETERS PP
                        WHERE RDB$PROCEDURE_NAME = @PROC
                        ORDER BY RDB$PARAMETER_NUMBER";

                    using var paramCmd = new FbCommand(paramSql, connection);
                    paramCmd.Parameters.AddWithValue("@PROC", procName);

                    using var paramReader = paramCmd.ExecuteReader();
                    while (paramReader.Read())
                    {
                        string paramName = paramReader.GetString(0).Trim();
                        string domain = paramReader.GetString(1).Trim();
                        bool isOut = paramReader.GetInt16(2) == 1;

                        writer.WriteLine($"-- {(isOut ? "OUT" : "IN")} {paramName} {domain}");
                    }

                    writer.WriteLine();
                    writer.WriteLine(procSource);
                    writer.WriteLine();
                }
            }

            Console.WriteLine("Eksport metadanych zakończony.");
        }

        /// <summary>
        /// Aktualizuje istniejącą bazę danych Firebird 5.0 na podstawie skryptów.
        /// </summary>
        public static void UpdateDatabase(string connectionString, string scriptsDirectory)
        {
            if (!Directory.Exists(scriptsDirectory))
                throw new DirectoryNotFoundException($"Nie znaleziono katalogu skryptów: {scriptsDirectory}");

            using var connection = new FbConnection(connectionString);
            connection.Open();

            // istniejące tabele
            var existingTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (var cmd = new FbCommand(
                "SELECT RDB$RELATION_NAME FROM RDB$RELATIONS WHERE RDB$SYSTEM_FLAG = 0",
                connection))
            using (var reader = cmd.ExecuteReader())
                while (reader.Read())
                    existingTables.Add(reader.GetString(0).Trim());

            var scriptFiles = Directory.GetFiles(scriptsDirectory, "*.sql")
                                       .OrderBy(f => f)
                                       .ToList();

            foreach (var file in scriptFiles)
            {
                Console.WriteLine($"Wykonywanie skryptu: {Path.GetFileName(file)}");

                var scriptText = File.ReadAllText(file);

                foreach (var statement in SplitStatements(scriptText))
                {
                    if (string.IsNullOrWhiteSpace(statement))
                        continue;

                    var trimmed = statement.TrimStart();

                    // Procedury
                    if (trimmed.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("-- PROCEDURE_META", StringComparison.OrdinalIgnoreCase))
                    {
                        // Generujemy pełny CREATE PROCEDURE
                        string procSql = RewriteProcedure(statement);
                        string procName = statement.Split([' ', '('], StringSplitOptions.RemoveEmptyEntries)[2];

                        // DROP PROCEDURE
                        using (var transaction = connection.BeginTransaction())
                        {
                            try
                            {
                                new FbCommand($"DROP PROCEDURE {procName}", connection, transaction).ExecuteNonQuery();
                            }
                            catch { }
                            transaction.Commit();
                        }

                        // CREATE PROCEDURE
                        using (var transaction = connection.BeginTransaction())
                        {
                            new FbCommand(procSql, connection, transaction).ExecuteNonQuery();
                            transaction.Commit();
                        }

                        continue;
                    }


                    // Tabele
                    if (trimmed.StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase))
                    {
                        var tableName = trimmed.Split([' ', '\t', '('],
                            StringSplitOptions.RemoveEmptyEntries)[2];

                        if (!existingTables.Contains(tableName))
                        {
                            using var transaction = connection.BeginTransaction();
                            new FbCommand(statement, connection, transaction).ExecuteNonQuery();
                            transaction.Commit();

                            existingTables.Add(tableName);
                            continue;
                        }

                        // kolumny z bazy
                        var dbColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        using (var cmd = new FbCommand(
                            "SELECT RDB$FIELD_NAME FROM RDB$RELATION_FIELDS WHERE RDB$RELATION_NAME=@T",
                            connection))
                        {
                            cmd.Parameters.AddWithValue("@T", tableName.ToUpperInvariant());
                            using var r = cmd.ExecuteReader();
                            while (r.Read())
                                dbColumns.Add(r.GetString(0).Trim());
                        }

                        // kolumny z pliku SQL
                        var sqlColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                        int start = statement.IndexOf('(');
                        int end = statement.LastIndexOf(')');

                        if (start > 0 && end > start)
                        {
                            var defs = statement.Substring(start + 1, end - start - 1)
                                .Split([",\r\n", ",\n"], StringSplitOptions.RemoveEmptyEntries);

                            foreach (var def in defs)
                            {
                                var colName = def.Trim().Split(' ', '\t')[0];
                                sqlColumns[colName] = def.Trim();
                            }
                        }

                        // ADD COLUMN
                        foreach (var col in sqlColumns)
                        {
                            if (!dbColumns.Contains(col.Key))
                            {
                                using var transaction = connection.BeginTransaction();
                                new FbCommand(
                                    $"ALTER TABLE {tableName} ADD {col.Value};",
                                    connection, transaction).ExecuteNonQuery();
                                transaction.Commit();
                            }
                        }

                        // DROP COLUMN
                        foreach (var col in dbColumns)
                        {
                            if (!sqlColumns.ContainsKey(col))
                            {
                                using var transaction = connection.BeginTransaction();
                                try
                                {
                                    new FbCommand(
                                        $"ALTER TABLE {tableName} DROP {col};",
                                        connection, transaction).ExecuteNonQuery();
                                    transaction.Commit();
                                }
                                catch
                                {
                                    transaction.Rollback();
                                }
                            }
                        }

                        continue;
                    }

                    // Domeny
                    try
                    {
                        using var transaction = connection.BeginTransaction();
                        new FbCommand(statement, connection, transaction).ExecuteNonQuery();
                        transaction.Commit();
                    }
                    catch (FbException ex)
                    {
                        if (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
                            ex.Message.Contains("violation of PRIMARY or UNIQUE KEY constraint",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine($"Ignorowany błąd: {ex.Message}");
                        }
                        else
                            throw;
                    }
                }
            }

            Console.WriteLine("Aktualizacja bazy zakończona pomyślnie.");
        }

        public static string RewriteProcedure(string procedureBlock)
        {
            var lines = procedureBlock.Split(["\r\n", "\n"], StringSplitOptions.None);

            string? procName = null;
            var paramList = new List<string>();
            var bodyLines = new List<string>();

            foreach (var line in lines)
            {
                string trimmed = line.Trim();

                if (trimmed.StartsWith("-- PROCEDURE_META", StringComparison.OrdinalIgnoreCase))
                {
                    // nazwa procedury: -- PROCEDURE_META SP_GET_CUSTOMER_BALANCE
                    procName = trimmed.Split(' ')[2].Trim();
                }
                else if (trimmed.StartsWith("-- IN", StringComparison.OrdinalIgnoreCase))
                {
                    // parametr IN: -- IN CUSTOMER_ID DM_SHORT
                    paramList.Add(trimmed[5..].Trim());
                }
                else if (trimmed.StartsWith("-- OUT", StringComparison.OrdinalIgnoreCase))
                {
                    // parametr OUT: -- OUT TOTAL_AMOUNT DM_AMOUNT
                    paramList.Add(trimmed[6..].Trim());
                }
                else
                {
                    // reszta to ciało procedury
                    bodyLines.Add(line);
                }
            }

            if (procName == null)
                throw new InvalidOperationException("Nie znaleziono PROCEDURE_META w bloku procedury.");

            string header = paramList.Count == 0
                ? $"CREATE PROCEDURE {procName} AS"
                : $"CREATE PROCEDURE {procName} ({string.Join(", ", paramList)}) AS";

            return header + "\n" + string.Join("\n", bodyLines);
        }

        // SPLIT STATEMENTS
        public static IEnumerable<string> SplitStatements(string script)
        {
            if (string.IsNullOrWhiteSpace(script))
                yield break;

            var sb = new System.Text.StringBuilder();
            bool inProcedure = false;

            foreach (var line in script.Split(["\r\n", "\n"], StringSplitOptions.None))
            {
                string trimmed = line.Trim();

                if (trimmed.StartsWith("SET TERM", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (trimmed.StartsWith("CREATE PROCEDURE", StringComparison.OrdinalIgnoreCase))
                    inProcedure = true;

                sb.AppendLine(line);

                if (!inProcedure && trimmed.EndsWith(";"))
                {
                    yield return sb.ToString().Trim();
                    sb.Clear();
                }
                else if (inProcedure && trimmed.Equals("END", StringComparison.OrdinalIgnoreCase))
                {
                    yield return sb.ToString().Trim();
                    sb.Clear();
                    inProcedure = false;
                }
            }

            if (sb.Length > 0)
                yield return sb.ToString().Trim();
        }
    }
}
