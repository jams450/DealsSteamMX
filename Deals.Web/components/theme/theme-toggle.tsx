"use client";

import { Moon, Sun } from "lucide-react";
import { useEffect, useState } from "react";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/ui/cn";

type Theme = "light" | "dark";
type ThemeToggleProps = { className?: string };

function getThemeFromDocument(): Theme { return document.documentElement.classList.contains("dark") ? "dark" : "light"; }

export function ThemeToggle({ className }: ThemeToggleProps) {
  const [theme, setTheme] = useState<Theme>("light");
  const [mounted, setMounted] = useState(false);
  useEffect(() => { setTheme(getThemeFromDocument()); setMounted(true); }, []);
  function toggleTheme() {
    const currentTheme = getThemeFromDocument();
    const nextTheme = currentTheme === "dark" ? "light" : "dark";
    document.documentElement.classList.toggle("dark", nextTheme === "dark");
    localStorage.setItem("theme", nextTheme);
    setTheme(nextTheme);
  }
  if (!mounted) return <div className={cn("h-10 w-28 rounded-xl border border-slate-200 bg-white/70 dark:border-slate-800 dark:bg-slate-900/70", className)} aria-hidden="true" />;
  const isDark = theme === "dark";
  return <Button type="button" variant="secondary" className={cn("h-10 px-3 text-xs", className)} onClick={toggleTheme} aria-label={isDark ? "Cambiar a modo claro" : "Cambiar a modo oscuro"} title={isDark ? "Modo claro" : "Modo oscuro"}>{isDark ? <Sun className="h-4 w-4" aria-hidden="true" /> : <Moon className="h-4 w-4" aria-hidden="true" />}<span>{isDark ? "Modo claro" : "Modo oscuro"}</span></Button>;
}
