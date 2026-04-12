import { Component } from 'react'

/**
 * Error boundary to catch React rendering errors and display a fallback UI.
 * Prevents the entire app from crashing when a component throws.
 */
export class ErrorBoundary extends Component {
  constructor(props) {
    super(props)
    this.state = { hasError: false, error: null }
  }

  static getDerivedStateFromError(error) {
    return { hasError: true, error }
  }

  componentDidCatch(error, errorInfo) {
    console.error('[ErrorBoundary] Caught error:', error, errorInfo)
  }

  handleReload = () => {
    window.location.reload()
  }

  render() {
    if (this.state.hasError) {
      const Fallback = this.props.fallback
      if (Fallback) {
        return <Fallback error={this.state.error} onReload={this.handleReload} />
      }
      return (
        <div className="min-h-screen bg-black text-white p-8 flex flex-col items-center justify-center">
          <h1 className="text-2xl font-bold mb-4 text-red-400">Something went wrong</h1>
          <p className="text-gray-300 mb-4 max-w-2xl text-center">
            An unexpected error occurred. You can try reloading the page to recover.
          </p>
          <button
            onClick={this.handleReload}
            className="px-6 py-2 bg-gray-700 hover:bg-gray-600 rounded-lg text-white font-medium transition-colors"
          >
            Reload page
          </button>
        </div>
      )
    }
    return this.props.children
  }
}
