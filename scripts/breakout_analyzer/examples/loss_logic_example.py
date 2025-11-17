"""
Loss Logic Example
Demonstrates how to use the new loss logic module for stop loss management and loss analysis
"""

import pandas as pd
import sys
import os

# Add the parent directory to the path to import the logic modules
sys.path.append(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from logic.loss_logic import LossManager, StopLossConfig, LossType, StopLossType

def main():
    """Demonstrate loss logic functionality"""
    
    print("=== Loss Logic Example ===\n")
    
    # 1. Create loss manager with custom configuration
    print("1. Creating Loss Manager with custom configuration...")
    loss_config = StopLossConfig(
        initial_multiplier=3.0,  # 3x target as initial stop
        t1_adjustment="break_even",  # Move to break-even on T1
        t2_adjustment="lock_profit",  # Lock profit on T2
        t2_profit_lock=0.65,  # Lock in 65% profit
        max_loss_per_trade=100.0,  # Max $100 loss per trade
        max_daily_loss=500.0,  # Max $500 loss per day
        risk_per_trade=0.02  # 2% risk per trade
    )
    
    loss_manager = LossManager(loss_config)
    print(f"✓ Loss Manager created with config: {loss_config}\n")
    
    # 2. Calculate initial stop loss
    print("2. Calculating initial stop loss...")
    entry_price = 4500.0
    direction = "Long"
    target_pts = 10.0
    instrument = "ES"
    
    initial_sl = loss_manager.calculate_initial_stop_loss(
        entry_price, direction, target_pts, instrument
    )
    print(f"Entry Price: {entry_price}")
    print(f"Target Points: {target_pts}")
    print(f"Initial Stop Loss: {initial_sl}")
    print(f"Stop Distance: {entry_price - initial_sl:.2f} points\n")
    
    # 3. Demonstrate T1 stop loss adjustment
    print("3. Demonstrating T1 stop loss adjustment...")
    t1_sl, t1_reason = loss_manager.adjust_stop_loss_t1(
        entry_price, direction, target_pts, instrument
    )
    print(f"T1 Adjusted Stop Loss: {t1_sl}")
    print(f"T1 Adjustment Reason: {t1_reason}\n")
    
    # 4. Demonstrate T2 stop loss adjustment
    print("4. Demonstrating T2 stop loss adjustment...")
    t2_sl, t2_reason = loss_manager.adjust_stop_loss_t2(
        entry_price, direction, target_pts, level_idx=0
    )
    print(f"T2 Adjusted Stop Loss: {t2_sl}")
    print(f"T2 Adjustment Reason: {t2_reason}\n")
    
    # 5. Simulate a losing trade
    print("5. Simulating a losing trade...")
    exit_price = 4480.0  # Price below stop loss
    exit_time = pd.Timestamp.now()
    
    # Check if stop loss was hit
    high = 4490.0
    low = 4475.0
    stop_hit = loss_manager.check_stop_loss_hit(high, low, initial_sl, direction)
    print(f"Stop Loss Hit: {stop_hit}")
    
    # Calculate loss amount
    loss_amount = loss_manager.calculate_loss_amount(
        entry_price, exit_price, direction, instrument
    )
    print(f"Loss Amount: {loss_amount:.2f} points")
    
    # Calculate loss percentage
    account_size = 10000.0
    loss_percentage = loss_manager.calculate_loss_percentage(loss_amount, account_size)
    print(f"Loss Percentage: {loss_percentage:.2f}% of account\n")
    
    # 6. Create comprehensive loss result
    print("6. Creating comprehensive loss result...")
    loss_result = loss_manager.create_loss_result(
        entry_price=entry_price,
        exit_price=exit_price,
        direction=direction,
        stop_loss_price=initial_sl,
        exit_time=exit_time,
        loss_type=LossType.STOP_LOSS,
        stop_loss_type=StopLossType.INITIAL,
        target_price=entry_price + target_pts,
        peak_price=entry_price + 5.0,  # Peak reached 5 points above entry
        instrument=instrument,
        account_size=account_size
    )
    
    print(f"Loss Type: {loss_result.loss_type.value}")
    print(f"Loss Amount: {loss_result.loss_amount:.2f} points")
    print(f"Loss Percentage: {loss_result.loss_percentage:.2f}%")
    print(f"Risk-Reward Ratio: {loss_result.risk_reward_ratio:.2f}")
    print(f"Max Drawdown: {loss_result.max_drawdown:.2f} points")
    print(f"Acceptable Loss: {loss_result.is_acceptable_loss}")
    print(f"Loss Reason: {loss_result.loss_reason}\n")
    
    # 7. Record the loss and get statistics
    print("7. Recording loss and getting statistics...")
    loss_manager.record_loss(loss_amount, exit_time, LossType.STOP_LOSS, "trade_001")
    
    # Simulate a few more losses
    loss_manager.record_loss(15.0, exit_time, LossType.TIME_EXPIRY, "trade_002")
    loss_manager.record_loss(8.0, exit_time, LossType.STOP_LOSS, "trade_003")
    
    stats = loss_manager.get_loss_statistics(days=30)
    print(f"Loss Statistics (30 days):")
    print(f"  Total Trades: {stats['total_trades']}")
    print(f"  Total Loss: {stats['total_loss']:.2f} points")
    print(f"  Average Loss: {stats['average_loss']:.2f} points")
    print(f"  Max Loss: {stats['max_loss']:.2f} points")
    print(f"  Loss Rate: {stats['loss_rate']:.2f} per day")
    print(f"  Worst Day: {stats['worst_day']:.2f} points\n")
    
    # 8. Analyze loss patterns
    print("8. Analyzing loss patterns...")
    patterns = loss_manager.analyze_loss_patterns()
    print(f"Loss Pattern Analysis:")
    for loss_type, type_stats in patterns['loss_types'].items():
        print(f"  {loss_type}:")
        print(f"    Count: {type_stats['count']}")
        print(f"    Total: {type_stats['total']:.2f}")
        print(f"    Average: {type_stats['average']:.2f}")
        print(f"    Max: {type_stats['max']:.2f}")
    print(f"  Most Common Loss Type: {patterns['most_common_loss_type']}\n")
    
    # 9. Get stop loss recommendations
    print("9. Getting stop loss recommendations...")
    recommendations = loss_manager.get_stop_loss_recommendations("ES", volatility=1.2)
    print(f"Stop Loss Recommendations for ES (volatility=1.2):")
    print(f"  Multiplier: {recommendations['multiplier']}")
    print(f"  Min Distance: {recommendations['min_distance']} points")
    print(f"  Max Distance: {recommendations['max_distance']} points\n")
    
    print("=== Loss Logic Example Complete ===")

if __name__ == "__main__":
    main()
