"use client";

import { FormEvent, useState } from "react";
import { useRouter } from "next/navigation";
import { Logo } from "@/components/brand/logo";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { ThemeToggle } from "@/components/theme/theme-toggle";

export default function LoginPage() {
  const router = useRouter();
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [showPassword, setShowPassword] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setError(null);
    setLoading(true);

    try {
      const response = await fetch("/api/auth/login", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ username, password })
      });

      if (!response.ok) {
        setError("Credenciales inválidas");
        return;
      }

      router.replace("/");
      router.refresh();
    } catch {
      setError("No se pudo conectar con el servidor");
    } finally {
      setLoading(false);
    }
  }

  return (
    <main className="tabler-page flex items-center justify-center px-4 py-12">
      <div className="tabler-page-gradient" />

      <div className="absolute right-4 top-4 z-10">
        <ThemeToggle />
      </div>

      <Card className="relative z-10 w-full max-w-md space-y-6 border-[var(--tabler-border-strong)]/70 bg-[var(--tabler-surface-1)]/95 p-5 shadow-[var(--tabler-shadow-md)] backdrop-blur">
        <header className="space-y-2 text-center">
          <div className="flex justify-center">
            <Logo markClassName="h-9 w-9" />
          </div>
          <h1 className="text-2xl font-semibold tracking-tight text-[var(--tabler-text)]">Inicia sesión</h1>
          <p className="text-sm text-[var(--tabler-text-soft)]">Ingresa con tus credenciales para continuar.</p>
        </header>

        <form onSubmit={onSubmit} className="space-y-4">
          <Input
            label="Usuario"
            value={username}
            onChange={(e) => setUsername(e.target.value)}
            type="text"
            required
            autoComplete="username"
            placeholder="ej. demo"
          />

          <Input
            label="Contraseña"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            type={showPassword ? "text" : "password"}
            required
            autoComplete="current-password"
            placeholder="••••••••"
            rightSlot={
              <Button
                type="button"
                variant="ghost"
                onClick={() => setShowPassword((value) => !value)}
                className="h-7 rounded-lg px-2.5 text-xs"
              >
                {showPassword ? "Ocultar" : "Mostrar"}
              </Button>
            }
          />

          {error ? <Alert variant="danger">{error}</Alert> : null}

          <Button type="submit" fullWidth loading={loading} loadingText="Ingresando...">
            Ingresar
          </Button>
        </form>
      </Card>
    </main>
  );
}
