# CafePOS.Api

## Run (no database setup needed)

```
dotnet run
```

Opens Swagger UI at the printed `http://localhost:5xxx/swagger`. Without a
connection string, the API runs against an **in-memory database** — data
resets every time you restart, but every endpoint works today.

## Connect to Supabase Postgres later

1. Get your connection string from Supabase → **Settings → Database →
   Connection string** (use the **Session mode / port 5432** string for this,
   not the transaction pooler — migrations need a direct connection).
2. Put it in `appsettings.Development.json` (untracked-friendly, keep secrets
   out of `appsettings.json`):
   ```json
   {
     "ConnectionStrings": {
       "CafePos": "Host=db.xxxxx.supabase.co;Port=5432;Database=postgres;Username=postgres;Password=yourpassword;SSL Mode=Require;Trust Server Certificate=true"
     }
   }
   ```
   Or set the env var `ConnectionStrings__CafePos` (double underscore).
3. Generate and apply the migration:
   ```
   dotnet ef migrations add InitialSchema
   dotnet ef database update
   ```
4. Restart `dotnet run` — it now talks to Supabase and seeds the catalog data
   into the real tables.

For production traffic, switch to the **transaction pooler (port 6543)**
connection string instead — but always run migrations against port 5432.
