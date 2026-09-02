# Inventory Manager — Snacks & Drinks

A full-stack inventory management app:

- **Backend:** .NET 10 Web API, EF Core (code-first) targeting **SQLite**
- **Frontend:** Angular (standalone components) + TypeScript
- **Features:** Product CRUD (snacks & drinks), categories, suppliers, stock adjustments with history, low-stock alerts, and receipt upload (image/PDF scans) with a gallery viewer.

## Project Structure

```
InventoryApp/
  backend/InventoryApi/      .NET 10 Web API
  frontend/inventory-app/    Angular app
```

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js 20+](https://nodejs.org) and npm
- Angular CLI: `npm install -g @angular/cli`

## 1. Run the Backend

```bash
cd backend/InventoryApi
dotnet restore
dotnet run
```

The API starts at **http://localhost:5000** (see `Properties/launchSettings.json`) and opens Swagger UI at `/swagger`.

The database (`inventory.db`, a SQLite file) is created automatically on first run via `DbContext.Database.EnsureCreated()` in `Data/DbInitializer.cs`, and is seeded with sample categories, a supplier, and a few snack/drink products.

> **Using EF Core Migrations instead (optional):** the project currently uses `EnsureCreated()` for zero-friction first-run setup. If you'd prefer versioned migrations instead (recommended as the schema evolves), install the EF tool and run:
> ```bash
> dotnet tool install --global dotnet-ef
> cd backend/InventoryApi
> dotnet ef migrations add InitialCreate
> dotnet ef database update
> ```
> Then replace the `DbInitializer.Seed` call's `EnsureCreated()` with a `context.Database.Migrate()` call.

Uploaded receipt files are stored on disk under `backend/InventoryApi/wwwroot/receipts/`.

## 2. Run the Frontend

In a separate terminal:

```bash
cd frontend/inventory-app
npm install
npm start
```

This starts the Angular dev server at **http://localhost:4200** and proxies all `/api/*` calls to the backend at `http://localhost:5000` (see `proxy.conf.json`), so make sure the backend is already running.

Open **http://localhost:4200** in your browser.

## API Overview

| Endpoint | Description |
|---|---|
| `GET/POST/PUT/DELETE /api/categories` | Category CRUD |
| `GET/POST/PUT/DELETE /api/suppliers` | Supplier CRUD |
| `GET/POST/PUT/DELETE /api/products` | Product CRUD (filter by `search`, `type`, `categoryId`, `supplierId`, `lowStockOnly`) |
| `GET /api/products/alerts/low-stock` | Products at or below their low-stock threshold |
| `GET/POST /api/products/{id}/stock` | Stock adjustment history / apply an adjustment |
| `GET /api/receipts` | List receipts (optionally `?supplierId=`) |
| `POST /api/receipts` | Upload a receipt (multipart form: `file`, `title`, `notes`, `totalAmount`, `deliveryCost`, `packageCost`, `purchaseDate`, `supplierId`) |
| `PUT /api/receipts/{id}` | Update receipt metadata (same fields as upload) |
| `GET /api/receipts/{id}/file` | Download/view the stored receipt scan |
| `DELETE /api/receipts/{id}` | Delete a receipt and its file |

## Notes

- Receipt uploads accept `.jpg`, `.jpeg`, `.png`, `.webp`, `.heic`, and `.pdf`, capped at 10 MB.
- CORS is pre-configured to allow `http://localhost:4200` to call the API during development.
- For production, build the Angular app (`npm run build`) and either serve the static output from the API's `wwwroot`, or host it separately and update CORS/API base URL accordingly.
