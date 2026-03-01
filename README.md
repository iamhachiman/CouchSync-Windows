# CouchSync-Windows 💻🛋️

This is the Windows client for CouchSync. It receives and displays notifications and data from your Android device over your local network.

## 🚀 Vision
CouchSync-Windows acts as the central hub for your connectivity, displaying notifications and data from your phone natively within the Windows environment. No middleman, no server, just a direct connection over your local router.

## 🌟 Implemented Features

### 🔔 Notification Receiver (Core)
*   **Direct Local Connection:** Receives notification data directly from the Android app via the local network.
*   **Toast Integration:** Displays real-time, interactive notifications using the native Windows Toast API.
*   **Pairing Mechanism:** Pairs securely with the Android app via a QR code or manual pairing code display.
*   **Modern Interface:** Professional Dark Mode UI built with Material Design Themes.

## ✨ Upcoming Features

### 📷 Virtual Camera
*   **PC Cam Support:** Receive video streams directly from your phone and use them as a virtual webcam.

## 🏗️ Technical Details
- **Platform:** Windows (C# / WPF)
- **Networking:** Local TCP Listener. Handles incoming JSON payloads from the Android counterpart.
- **UI Framework:** MaterialDesignInXamlToolkit.

---

*Part of the [CouchSync](https://github.com/iamhachiman/CouchSync) project.*
