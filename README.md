# Shed Security 🛡️

**Shed Security** is a lightweight, highly optimized server-side anti-cheat and administration mod for Vintage Story (v1.19 - v1.22+). 

It runs entirely on the server backend to validate player behavior, detect unauthorized client modifications, and provide server administrators with peace of mind—without impacting server tick rates.

### ❓ How is this different from ShedLink?
* **Shed Security** is the actual anti-cheat engine. It monitors player movement, validates client mods, and enforces server rules. It operates completely standalone.
* **ShedLink** is our separate network bridge mod that connects the server to an external Windows desktop dashboard. (If you use them together, they integrate seamlessly!)

---

## ✨ Key Features

### 🕵️ Anti-Cheat Engine
* **Movement Validation:** Actively monitors player positional data (`Entity.Pos`) every tick to detect and rubber-band speed hacks, fly hacks, and irregular teleports.
* **Client Mod Scanning:** Interrogates joining clients to verify their loaded mods against the server's approved list, kicking users with unauthorized client-side advantages.
* **Smart Thresholds:** Forgiving enough to account for server lag and high-ping players, but strict enough to catch blatant exploits.

### ⚡ Performance & Architecture
* **Zero-Overhead Compatibility:** Built using heavily optimized, cached reflection. A single compiled `.dll` dynamically adapts to breaking API changes (like Fields turning into Properties), allowing it to run flawlessly across Vintage Story versions 1.19 through 1.22+ without causing server lag.
* **Server-Side Only:** Players do not need to download this mod to join your server. It is completely invisible to the end-user unless they trigger a violation.

---

## 🚀 Installation (For Server Owners)

1. Download the latest `ShedSecurity.zip` from the [Releases](ShedSecurity) page.
2. Drop the `.zip` file directly into your Vintage Story server's `Mods` folder.
3. Restart your server. 
4. *(Optional)* Configure your violation thresholds and approved mod lists in the `ModConfig/shedsecurity.json` file generated after the first boot.

---

## 🛠️ Building from Source (For Developers)

We are open-sourcing Shed Security so the community can audit the checks and contribute new detection methods.

### Prerequisites
* Visual Studio 2022 (or your preferred C# IDE)
* .NET 8.0 SDK
* Vintage Story Server API (`VintagestoryAPI.dll`) from any 1.19+ installation.

### Compilation
1. Clone the repository.
2. Ensure your project references point to your local `VintagestoryAPI.dll`.
3. Build the solution in `Release` mode. 
4. The post-build events (if configured) or the `/bin/Release/` folder will contain your compiled `.dll` ready to be zipped into a mod package.

---

## 🤝 Contributing
Found a bypass or want to add a new check? Pull requests are highly encouraged! 
1. Fork the repo.
2. Create a feature branch (`git checkout -b feature/NewCheck`).
3. Ensure your checks respect the cached reflection architecture to maintain cross-version compatibility.
4. Open a Pull Request!
