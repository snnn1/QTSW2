/**
 * CopyableText component - displays text with copy button
 */
import { useState } from 'react'

interface CopyableTextProps {
  text: string
  className?: string
  textClassName?: string
  buttonClassName?: string
}

export function CopyableText({
  text,
  className = '',
  textClassName = '',
  buttonClassName = '',
}: CopyableTextProps) {
  const [copied, setCopied] = useState(false)

  const handleCopy = async () => {
    try {
      await navigator.clipboard.writeText(text)
      setCopied(true)
      setTimeout(() => setCopied(false), 2000)
    } catch (error) {
      console.error('Failed to copy:', error)
    }
  }

  return (
    <div className={`flex items-center gap-2 ${className}`}>
      <code className={`font-mono text-sm ${textClassName}`} title={text}>
        {text}
      </code>
      <button
        onClick={handleCopy}
        className={`px-2 py-1 text-xs bg-gray-700 hover:bg-gray-600 rounded ${buttonClassName}`}
        title="Copy to clipboard"
      >
        {copied ? 'OK' : 'Copy'}
      </button>
    </div>
  )
}
