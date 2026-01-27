# Watchdog Frontend

**Standalone frontend application** - completely independent from the dashboard.

## Structure

```
modules/watchdog/frontend/
├── src/
│   ├── components/        # Watchdog-specific components
│   ├── hooks/             # Watchdog hooks
│   ├── contexts/          # WebSocket context
│   ├── utils/             # Utility functions
│   ├── types/             # TypeScript types
│   ├── services/          # API services
│   ├── config/           # Configuration
│   ├── App.tsx           # Main app component
│   └── main.jsx          # Entry point
├── index.html            # HTML entry
├── package.json          # Dependencies (independent)
├── vite.config.js        # Vite config (port 5175)
└── README.md             # This file
```

## Development

```bash
cd modules/watchdog/frontend
npm install
npm run dev
```

Frontend runs on: **http://localhost:5175**

## Backend

Backend runs on: **http://localhost:8002**

## Separation

- ✅ **Independent package.json** - no shared dependencies
- ✅ **Independent node_modules** - separate install
- ✅ **Independent Vite config** - port 5175
- ✅ **No shared code** - all files copied, not linked
- ✅ **Can be deployed separately** - completely standalone

## Ports

- **Watchdog Backend**: 8002
- **Watchdog Frontend**: 5175
- **Dashboard Backend**: 8001
- **Dashboard Frontend**: 5173
- **Matrix Backend**: 8000
- **Matrix Frontend**: 5174
