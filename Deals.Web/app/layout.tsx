import type { Metadata } from "next";
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

export const metadata: Metadata = {
  title: "DealExt",
  description: "DealExt"
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
