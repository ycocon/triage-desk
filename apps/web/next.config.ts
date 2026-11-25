import type { NextConfig } from "next";

const config: NextConfig = {
  devIndicators: false,
  async headers() {
    return [
      { source: "/workspaces/:path*", headers: [{ key: "Cache-Control", value: "private, no-store" }] },
      { source: "/verify-email", headers: [{ key: "Referrer-Policy", value: "no-referrer" }] },
      { source: "/join", headers: [
        { key: "Referrer-Policy", value: "no-referrer" },
        { key: "Cache-Control", value: "private, no-store" },
      ] },
    ];
  },
};

export default config;
