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

## Razorpay (subscription checkout)

`POST /api/payments/create-order` + `POST /api/payments/verify` back the Upgrade
button on the app's Subscription screen (Razorpay Standard Checkout — see
`Controllers/PaymentsController.cs`). Both are Owner-only, and the plan is only
applied after Razorpay's signature is re-computed server-side.

Leave the keys unset and the feature stays off: the endpoints answer "online
payments aren't set up yet" and the app keeps showing the old *Contact to
Upgrade* message. Nothing else in the product depends on it.

Credentials **never** go in `appsettings.json` / `appsettings.Development.json`
— both are committed. Locally:

```
dotnet user-secrets set "Razorpay:KeyId"     "rzp_test_xxxxxxxxxxxx"
dotnet user-secrets set "Razorpay:KeySecret" "xxxxxxxxxxxxxxxxxxxxxx"
```

On Render, set the env vars `Razorpay__KeyId` and `Razorpay__KeySecret`
(double underscore). Get both from Razorpay Dashboard → **Account & Settings →
API Keys**; test keys start `rzp_test_`, live keys `rzp_live_`. The **key id** is
public — the browser receives it in the create-order response, which is why the
web build has no copy of its own. The **key secret** is both the API password and
the HMAC key that makes a payment signature mean anything, so it must never
reach the frontend or the repository.

Prices live in `Infrastructure/SubscriptionPricing.cs` and must stay in step
with `GRID_PLANS` in the app's `SubscriptionScreen.tsx`.
