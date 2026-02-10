# DbMetaTool

Opis programu:

DbMetaTool to narzędzie konsolowe do zarządzania metadanymi baz Firebird 5.0.  

Umożliwia:
* Budowę nowej bazy na podstawie skryptów SQL.
* Eksport metadanych (domeny, tabele, procedury) do plików SQL.
* Aktualizację istniejącej bazy różnicowo, z zachowaniem struktur tabel i aktualizacją procedur.

Wymagania:
* Firebird 5.0
* .NET 8 lub nowszy
* Biblioteka NuGet: `FirebirdSql.Data.FirebirdClient`

Sposób użycia (w konsoli):
```bash
DbMetaTool build-db --db-dir "C:\\db\\fb5" --scripts-dir "C:\\scripts"
DbMetaTool export-scripts --connection-string "User=SYSDBA;Password=masterkey;Database=C:\\db\\fb5\\database.fdb;DataSource=localhost;Port=3050;Dialect=3;Charset=UTF8;" --output-dir "C:\\out"
DbMetaTool update-db --connection-string "User=SYSDBA;Password=masterkey;Database=C:\\db\\fb5\\database.fdb;DataSource=localhost;Port=3050;Dialect=3;Charset=UTF8;" --scripts-dir "C:\\out"
```

## Wykryte błędy/niedociągnięcia:
* W bazie test.fdb parametr BALANCE jest ustawiony jako "out", a parametr CUSTOMERID jako "in". W bazie database.fdb, w procedurze SP\_GET\_CUSTOMER\_BALANCE oba parametry są ustawione jako "in".
* Pisanie zapytań SQL bezpośrednio przez FlameRobin również nie pozwala na prawidłowe ustawianie parametrów in/out.
* Metoda UpdateDatabase nie działa prawidłowo. Program nie radzi sobie z procedurami.

## Opisy plików i folderów:
* test.fdb - pierwsza baza, stworzona ręcznie, później modyfikowana ręcznie przez zmiany w tabelach i procedurach
* test\_backup.fbk - backup bazy test.fdb, stworzony przed wprowadzeniem do niej ręcznych zmian
* test\_restore.fdb - baza odtworzona z pliku test\_backup.fbk, na niej próbowałem używać UpdateDatabase oraz skryptów z exported\_scripts\_updated
* database.fdb - baza powstała przy użyciu funkcji BuildDatabase i skryptów z exported\_scripts
* exported\_scripts - folder ze skryptami wyeksportowanymi z test.fdb PRZED modyfikowaniem bazy ręcznie
* exported\_scripts\_updated - folder ze skryptami wyeksportowanymi z test.fdb PO modyfikowaniu bazy ręcznie
