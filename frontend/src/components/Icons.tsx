// Inline stroke icons (decorative: aria-hidden). currentColor so they follow text colour.
import type { ReactNode } from 'react'

function Svg({ children, size = 16, width = 2 }: { children: ReactNode; size?: number; width?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={width}
      strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      {children}
    </svg>
  )
}

type P = { size?: number }

export const Logo = ({ size = 18 }: P) => (
  <Svg size={size} width={2.2}><path d="M8 6l-5 6 5 6" /><path d="M16 6l5 6-5 6" /><path d="M13.5 4l-3 16" /></Svg>
)
export const DiffIcon = ({ size }: P) => (
  <Svg size={size}><path d="M12 3v14" /><path d="M5 10h14" /><path d="M5 21h14" /></Svg>
)
export const PrIcon = ({ size }: P) => (
  <Svg size={size}>
    <circle cx="6" cy="6" r="2.5" /><circle cx="6" cy="18" r="2.5" /><circle cx="18" cy="18" r="2.5" />
    <path d="M6 8.5v7" /><path d="M18 15.5V9a3 3 0 0 0-3-3h-4" /><path d="M13 3.5L10.5 6 13 8.5" />
  </Svg>
)
export const LinkIcon = ({ size }: P) => (
  <Svg size={size}>
    <path d="M10 13a5 5 0 0 0 7.5.5l3-3a5 5 0 0 0-7-7l-1.7 1.7" /><path d="M14 11a5 5 0 0 0-7.5-.5l-3 3a5 5 0 0 0 7 7l1.7-1.7" />
  </Svg>
)
export const Arrow = ({ size }: P) => <Svg size={size} width={2.2}><path d="M5 12h14" /><path d="M13 6l6 6-6 6" /></Svg>
export const Chevron = ({ size }: P) => <Svg size={size}><path d="M6 9l6 6 6-6" /></Svg>
export const ChevronRight = ({ size }: P) => <Svg size={size}><path d="M9 6l6 6-6 6" /></Svg>
export const Plus = ({ size }: P) => <Svg size={size} width={2.4}><path d="M12 5v14" /><path d="M5 12h14" /></Svg>
export const Copy = ({ size }: P) => (
  <Svg size={size}><rect x="9" y="9" width="11" height="11" rx="2" /><path d="M5 15V5a2 2 0 0 1 2-2h10" /></Svg>
)
export const Check = ({ size }: P) => <Svg size={size} width={3}><path d="M5 12l5 5 9-10" /></Svg>
export const Download = ({ size }: P) => <Svg size={size}><path d="M12 4v11" /><path d="M7 10l5 5 5-5" /><path d="M5 20h14" /></Svg>
export const Wrench = ({ size }: P) => (
  <Svg size={size} width={2.4}><path d="M14.7 6.3a4 4 0 0 0-5.4 5.4L4 17l3 3 5.3-5.3a4 4 0 0 0 5.4-5.4l-2.5 2.5-2.4-.6-.6-2.4z" /></Svg>
)
export const Bulb = ({ size }: P) => (
  <Svg size={size} width={2.4}><path d="M9 18h6" /><path d="M10 21h4" /><path d="M12 3a6 6 0 0 0-3.5 10.9V16h7v-2.1A6 6 0 0 0 12 3z" /></Svg>
)
export const Info = ({ size }: P) => <Svg size={size}><circle cx="12" cy="12" r="9" /><path d="M12 8v5" /><path d="M12 16h.01" /></Svg>
export const Warning = ({ size }: P) => <Svg size={size} width={2.2}><path d="M12 3l9 16H3z" /><path d="M12 10v4" /><path d="M12 17h.01" /></Svg>
export const Shield = ({ size }: P) => <Svg size={size}><path d="M12 3l8 3v6c0 4.5-3.4 8.2-8 9-4.6-.8-8-4.5-8-9V6z" /><path d="M9 12l2 2 4-4" /></Svg>
export const Retry = ({ size }: P) => <Svg size={size}><path d="M3 12a9 9 0 1 0 3-6.7" /><path d="M3 4v5h5" /></Svg>

export const CategoryIcon = ({ category, size = 17 }: { category: string; size?: number }) => {
  switch (category) {
    case 'bug':
      return (
        <Svg size={size}>
          <rect x="7" y="7" width="10" height="13" rx="5" /><path d="M12 7V4" /><path d="M4 12h3" /><path d="M17 12h3" />
          <path d="M5 18l2-1.5" /><path d="M19 18l-2-1.5" />
        </Svg>
      )
    case 'risk':
      return <Svg size={size}><path d="M12 3l8 3v6c0 4.5-3.4 8.2-8 9-4.6-.8-8-4.5-8-9V6z" /><path d="M12 8v5" /><path d="M12 16.5h.01" /></Svg>
    case 'missing_test':
      return <Svg size={size}><path d="M9 3h6" /><path d="M10 3v6L5 18a2 2 0 0 0 1.8 3h10.4a2 2 0 0 0 1.8-3l-5-9V3" /><path d="M7.5 14h9" /></Svg>
    default:
      return <Svg size={size}><path d="M4 20l4-1 11-11-3-3L5 16z" /><path d="M14 6l3 3" /></Svg>
  }
}
