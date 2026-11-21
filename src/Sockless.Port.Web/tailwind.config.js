// Tailwind maps Sockless semantic roles (--sl-*), never literal colours.
// Roles with an -rgb triplet in workshop.css support opacity modifiers (bg-accent/10);
// the rest are plain variables and must not be used with /NN modifiers.
const rgb = (name) => `rgb(var(--sl-${name}-rgb) / <alpha-value>)`;

/** @type {import('tailwindcss').Config} */
module.exports = {
  content: ['./Pages/**/*.cshtml', './Infrastructure/**/*.cs'],
  theme: {
    extend: {
      fontFamily: {
        heading: ['Oswald', 'Raleway', 'sans-serif'],
        sans: ['Raleway', 'system-ui', 'sans-serif'],
        mono: ['"Roboto Mono"', 'ui-monospace', 'monospace'],
      },
      colors: {
        bg: 'var(--sl-bg)',
        surface: 'var(--sl-surface)',
        card: 'var(--sl-card)',
        raised: 'var(--sl-raised)',
        line: 'var(--sl-border)',
        'line-accent': 'var(--sl-border-accent)',
        heading: 'var(--sl-text-heading)',
        body: 'var(--sl-text)',
        secondary: 'var(--sl-text-secondary)',
        muted: 'var(--sl-text-muted)',
        accent: rgb('accent'),
        'accent-hover': 'var(--sl-accent-hover)',
        'accent-line': 'var(--sl-accent-line)',
        'accent-2': rgb('accent-2'),
        success: rgb('success'),
        warning: rgb('warning'),
        danger: rgb('danger'),
        info: rgb('info'),
      },
      borderRadius: {
        xs: 'var(--sl-radius-xs)',
        sm: 'var(--sl-radius-sm)',
        DEFAULT: 'var(--sl-radius-sm)',
        md: 'var(--sl-radius-md)',
        lg: 'var(--sl-radius-lg)',
      },
      boxShadow: {
        menu: 'var(--sl-shadow-menu)',
      },
      transitionDuration: {
        fast: 'var(--sl-duration-fast)',
        normal: 'var(--sl-duration-normal)',
      },
    },
  },
  plugins: [],
};
