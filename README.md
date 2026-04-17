# Restore

A full-stack e-commerce web application built with ASP.NET Core and React + TypeScript. Restore offers a complete online shopping experience with product browsing, basket management, secure checkout, order tracking, and an AI-powered customer support chatbot.

---

## Table of Contents

- [Features](#features)
- [Tech Stack](#tech-stack)
- [Project Structure](#project-structure)
- [Getting Started](#getting-started)
  - [Prerequisites](#prerequisites)
  - [1. Start the Database](#1-start-the-database)
  - [2. Run the API](#2-run-the-api)
  - [3. Run the Frontend](#3-run-the-frontend)
- [Environment Variables & Secrets](#environment-variables--secrets)
- [AI Chatbot](#ai-chatbot)
- [Deployment (Azure)](#deployment-azure)
- [Key API Endpoints](#key-api-endpoints)

---

## Features

### Storefront
- Product catalog with categories, variants, and search/filter/sort/pagination
- Product detail pages with image gallery
- Shopping basket (persisted per user/session)
- Favorites / wishlist
- Promotional discounts and coupon codes
- Configurable shipping rates

### Checkout & Payments
- Secure checkout flow powered by [Stripe](https://stripe.com)
- Order confirmation and order history
- Invoice PDF generation per order
- Stripe payment reversal support

### Accounts & Auth
- Email/password registration and login (ASP.NET Identity)
- Google OAuth sign-in
- User profile and address management
- Account deletion

### Admin Panel
- Product CRUD with Cloudinary image uploads
- Category management
- Order management and analytics dashboard (Recharts)
- Newsletter campaigns (SendGrid)
- Hero block / homepage banner editor
- Promo / coupon management
- UI settings (logo, colours, etc.)
- Campaign management
- Notification system

### AI Customer Support Chatbot
- Powered by Anthropic Claude
- Responds in Portuguese (configurable via system prompt)
- Rate-limited: 30 requests per 10 minutes
- See [CHATBOT_SETUP.md](CHATBOT_SETUP.md) for configuration details

### Other
- Transactional emails via SendGrid
- Contact form
- In-app user notifications
- Health-check endpoint

---

## Tech Stack

| Layer | Technology |
|---|---|
| **Backend** | ASP.NET Core 8, C# |
| **ORM** | Entity Framework Core (SQL Server) |
| **Auth** | ASP.NET Identity, Google OAuth |
| **Payments** | Stripe |
| **Email** | SendGrid |
| **Image Storage** | Cloudinary |
| **AI Chatbot** | Anthropic Claude (claude-opus-4-6) |
| **Frontend** | React 18, TypeScript, Vite |
| **State Management** | Redux Toolkit |
| **UI** | Material UI (MUI v6) |
| **Routing** | React Router v6 |
| **Forms** | React Hook Form + Zod |
| **Charts** | Recharts |
| **Database** | SQL Server 2022 (Docker / Azure SQL) |
| **Containerisation** | Docker Compose |

---

## Project Structure

```
RestoreV2/
├── API/                        # ASP.NET Core Web API
│   ├── Controllers/            # REST controllers (products, basket, orders, admin, chat, …)
│   ├── Data/                   # EF Core DbContext and seed data
│   ├── DTOs/                   # Data Transfer Objects
│   ├── Entities/               # Domain entities (Product, Order, User, …)
│   ├── Migrations/             # EF Core database migrations
│   ├── Services/               # Business logic (Payments, Email, Chatbot, Invoicing, …)
│   ├── Middleware/             # Global exception handler
│   ├── RequestHelpers/         # Pagination, filtering helpers
│   ├── Program.cs              # App bootstrap & DI configuration
│   └── appsettings.*.json      # Environment-specific configuration
├── client/                     # React + TypeScript frontend
│   ├── src/
│   │   ├── app/                # Store, router, shared components, layouts
│   │   ├── features/           # Feature modules (catalog, basket, checkout, admin, …)
│   │   └── lib/                # Shared utilities and hooks
│   ├── index.html
│   └── vite.config.ts
├── docker-compose.yml          # SQL Server container
├── CHATBOT_SETUP.md            # Chatbot API key configuration guide
└── Restore.sln                 # .NET solution file
```

---

## Getting Started

### Prerequisites

| Tool | Version |
|---|---|
| [.NET SDK](https://dotnet.microsoft.com/download) | 8.0+ |
| [Node.js](https://nodejs.org/) | 18+ |
| [Docker Desktop](https://www.docker.com/products/docker-desktop/) | Any recent version |

### 1. Start the Database

```bash
docker-compose up -d
```

This starts SQL Server 2022 on port `1433` with the password `Password@1`.

### 2. Run the API

```bash
cd API
dotnet run
```

The API will start on `https://localhost:5001` (or as configured). On first run, EF Core migrations are applied automatically and the database is seeded with sample data.

**Required secrets** – set these as environment variables or in `appsettings.Development.json` (never commit real keys):

| Variable | Description |
|---|---|
| `ANTHROPIC_API_KEY` | Anthropic API key for the chatbot |
| `StripeSettings__SecretKey` | Stripe secret key |
| `StripeSettings__WhSecret` | Stripe webhook secret |
| `EmailSettings__SendGridApiKey` | SendGrid API key |
| `Cloudinary__ApiSecret` | Cloudinary API secret |

### 3. Run the Frontend

```bash
cd client
npm install
npm run dev
```

The app will be available at `https://localhost:3000`.

---

## Environment Variables & Secrets

Copy `.env.local` at the root to `.env` and fill in any values needed for local development. The `.env` file is already listed in `.gitignore` and must **never** be committed.

For the chatbot API key specifically, see [CHATBOT_SETUP.md](CHATBOT_SETUP.md).

---

## AI Chatbot

The built-in customer support widget calls `POST /api/chat` and proxies the message to Anthropic's Claude model. Key settings (in `appsettings.json`):

```json
"Chatbot": {
  "Enabled": true,
  "SystemPrompt": "..."   // Portuguese customer support prompt
},
"Anthropic": {
  "Model": "claude-opus-4-6",
  "MaxTokens": 1000,
  "Temperature": 0.2
}
```

The endpoint is rate-limited to **30 requests per 10 minutes** per client.

See [CHATBOT_SETUP.md](CHATBOT_SETUP.md) for full setup and troubleshooting instructions.

---

## Deployment (Azure)

The application is configured for Azure App Service:

1. **Database** – Azure SQL (set `ConnectionStrings__DefaultConnection` in App Service → Configuration → Connection strings).
2. **Secrets** – Add all secret settings under App Service → Configuration → Application settings.
3. **Build & publish** – The Vite build output is copied into `API/wwwroot` so the .NET app serves the SPA as static files.
4. **Forwarded headers** – Already configured for Azure reverse proxies.

---

## Key API Endpoints

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/products` | List products (with filtering, sorting, pagination) |
| `GET` | `/api/products/{id}` | Product detail |
| `GET/POST/DELETE` | `/api/basket` | Basket management |
| `GET/POST` | `/api/orders` | Place and retrieve orders |
| `POST` | `/api/payments` | Create/update Stripe payment intent |
| `POST` | `/api/chat` | AI chatbot |
| `POST` | `/api/account/register` | User registration |
| `POST` | `/api/login` | User login |
| `GET` | `/api/admin/products` | Admin product list |
| `POST` | `/api/admin/products` | Create product |
| `PUT` | `/api/admin/products/{id}` | Update product |
| `DELETE` | `/api/admin/products/{id}` | Delete product |
| `GET` | `/api/health` | Health check |
