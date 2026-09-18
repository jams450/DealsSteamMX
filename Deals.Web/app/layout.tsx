import type { Metadata, Viewport } from "next";
import "./globals.css";

const themeInitScript = `
(() => {
  try {
    const saved = localStorage.getItem("theme");
    const shouldUseDark = saved ? saved === "dark" : true;
    document.documentElement.classList.toggle("dark", shouldUseDark);

    const savedPalette = localStorage.getItem("paletteTheme");
    const palette = savedPalette === "blue" || savedPalette === "light-blue" ? savedPalette : "light-blue";
    document.documentElement.setAttribute("data-theme", palette);
  } catch {}
})();
`;

const description =
  "Comparador de precios de juegos para México: precio directo de Steam en MXN, ofertas de IsThereAnyDeal y GG.deals, y conversión aproximada a pesos con la tasa del día.";

export const metadata: Metadata = {
  title: "Deals Steam MX",
  description,
  icons: {
    icon: [{ url: "/icon.svg", type: "image/svg+xml" }]
  },
  openGraph: {
    title: "Deals Steam MX",
    description,
    siteName: "Deals Steam MX",
    type: "website",
    locale: "es_MX"
  },
  twitter: {
    card: "summary",
    title: "Deals Steam MX",
    description
  }
};

// Browser chrome color lives outside the page, so it cannot use `var(--tabler-page-bg)`.
// Values mirror that token in both themes.
export const viewport: Viewport = {
  themeColor: [
    { media: "(prefers-color-scheme: dark)", color: "#0d0f13" },
    { media: "(prefers-color-scheme: light)", color: "#f1f5f9" }
  ]
};

export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="es" suppressHydrationWarning>
      <head>
        <script dangerouslySetInnerHTML={{ __html: themeInitScript }} />
      </head>
      <body>{children}</body>
    </html>
  );
}
