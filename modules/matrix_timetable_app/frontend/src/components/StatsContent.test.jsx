import { describe, it, expect, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import StatsContent from './StatsContent'

describe('StatsContent', () => {
  const mockStats = {
    totalProfitDollars: '$1,234',
    totalProfit: '1234',
    executedTradesTotal: 100,
    executedTradesAllowed: 90,
    executedTradesFiltered: 10,
    winRate: '55.0',
    wins: 55,
    losses: 40,
    breakEven: 5,
    profitFactor: '1.5',
    rrRatio: '1.2',
    avgTradesPerDay: '2.5',
    profitPerDay: '$50',
    profitPerWeek: '$350',
    profitPerMonth: '$1,500',
    profitPerYear: '$18,000',
    profitPerTrade: '$12',
    sharpeRatio: '1.2',
    sortinoRatio: '1.5',
    calmarRatio: '0.8',
    maxDrawdownDollars: '$500',
    maxConsecutiveLosses: 5
  }

  it('renders master stats with toggle when streamId is master', () => {
    const setIncludeFilteredExecuted = vi.fn()
    render(
      <StatsContent
        stats={mockStats}
        streamId="master"
        includeFilteredExecuted={true}
        setIncludeFilteredExecuted={setIncludeFilteredExecuted}
      />
    )
    expect(screen.getByText('Statistics Settings')).toBeInTheDocument()
    expect(screen.getByText('Include filtered executed trades')).toBeInTheDocument()
    expect(screen.getByText('$1,234')).toBeInTheDocument()
    expect(screen.getByText('100')).toBeInTheDocument()
  })

  it('renders individual stream stats when streamId is not master', () => {
    render(
      <StatsContent
        stats={mockStats}
        streamId="ES1"
      />
    )
    expect(screen.getByText('Core Performance')).toBeInTheDocument()
    expect(screen.getByText('$1,234')).toBeInTheDocument()
    expect(screen.getByText('55.0%')).toBeInTheDocument()
  })

  it('returns null when stats is null', () => {
    const { container } = render(
      <StatsContent stats={null} streamId="master" />
    )
    expect(container.firstChild).toBeNull()
  })
})
